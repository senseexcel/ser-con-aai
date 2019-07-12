#region License
/*
Copyright (c) 2018 Konrad Mattheis und Martin Berthold
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
#endregion

namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.IO.Compression;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using System.IO;
    using Grpc.Core;
    using Hjson;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using NLog;
    using Ser.Api;
    using Ser.Distribute;
    using Q2g.HelperQrs;
    using Q2g.HelperPem;
    using Qlik.EngineAPI;
    using Qlik.Sse;
    using Q2g.HelperQlik;
    using static Qlik.Sse.Connector;
    using System.Text;
    using YamlDotNet.Serialization;
    #endregion

    public class SerEvaluator : ConnectorBase, IDisposable
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Enumerator
        public enum SerFunction
        {
            START = 1,
            STATUS = 2,
            STOP = 3,
        }

        public enum ConnectorState
        {
            ERROR = -1,
            NOTHING = 0,
            RUNNING = 1,
            REPORTING_SUCCESS = 2,
            DELIVERYSUCCESS = 3,
            DOWNLOADLINKAVAILABLE = 4
        }

        public enum EngineResult
        {
            SUCCESS,
            ERROR,
            ABORT,
            WARNING,
            UNKOWN
        }
        #endregion

        #region Properties & Variables
        private static SerOnDemandConfig onDemandConfig;
        private SessionManager sessionManager;
        private Ser.Engine.Rest.Client.SerApiClient restClient;
        private ConcurrentDictionary<Guid, ActiveTask> runningTasks;
        private object threadObject = new object();
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            onDemandConfig = config;
            ValidationCallback.Connection = config.Connection;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += ValidationCallback.ValidateRemoteCertificate;
            restClient = new Ser.Engine.Rest.Client.SerApiClient(new HttpClient());
            var baseUri = new Uri(restClient.BaseUrl);
            var baseUrl = $"{config.RestServiceUrl}{baseUri.PathAndQuery}";
            restClient.BaseUrl = baseUrl;
            sessionManager = new SessionManager();
            runningTasks = new ConcurrentDictionary<Guid, ActiveTask>();
            Cleanup(); 
        }
        #endregion

        #region Private Methods
        private void Cleanup()
        {
            Task.Delay(250).ContinueWith((res) => restClient.DeleteAllFilesAsync());
        }

        private void CheckRestService()
        {
            try
            {
                //Check rest service health
                logger.Debug("Check rest service connection...");
                var healthResult = restClient.HealthStatusAsync().Result;
                if (healthResult.Success.Value)
                    logger.Info("The communication with ser rest service was successfully.");
                else
                    throw new Exception("The rest service is not available.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"No connection to rest service \"{onDemandConfig.RestServiceUrl}\" Details: {ex.Message}.'");
            }
        }

        private JObject CreateAuditMatrixParameters(DomainUser user)
        {
            return JObject.FromObject(new
            {
                resourceType = "App",
                resourceRef = new { },
                subjectRef = new
                {
                    resourceFilter = $"userid eq '{user.UserId}' and userdirectory eq '{user.UserDirectory}'"
                },
                actions = 46,
                environmentAttributes = "",
                subjectProperties = new string[] { "id", "name", "userId", "userDirectory" },
                auditLimit = 1000,
                outputObjectsPrivileges = 2
            });
        }

        private async Task<DomainUser> GetAppOwner(QlikQrsHub qrsHub, string appId)
        {
            DomainUser resultUser = null;
            try
            {
                var qrsResult = await qrsHub.SendRequestAsync($"app/{appId}", HttpMethod.Get);
                logger.Trace($"appResult:{qrsResult}");
                dynamic jObject = JObject.Parse(qrsResult);
                string userDirectory = jObject?.owner?.userDirectory ?? null;
                string userId = jObject?.owner?.userId ?? null;
                if (!String.IsNullOrEmpty(userDirectory) && !String.IsNullOrEmpty(userId))
                {
                    resultUser = new DomainUser($"{userDirectory}\\{userId}");
                    logger.Debug($"Found app owner: {resultUser.ToString()}");
                }
                else
                    logger.Error($"No user directory {userDirectory} or user id {userId} found.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The app owner could not found.");
            }
            return resultUser;
        }

        private async Task<bool> HasReadRights(QlikQrsHub qrsHub, ActiveTask task)
        {
            try
            {
                logger.Debug($"Search for read right of user {task.Session.User.ToString()}");
                var parameter = CreateAuditMatrixParameters(task.Session.User);
                var contentData = Encoding.UTF8.GetBytes(parameter.ToString());
                var data = new ContentData()
                {
                    ContentType = "application/json",
                    FileData = contentData,
                };
                var matrixResponse = await qrsHub.SendRequestAsync("systemrule/security/audit/matrix", HttpMethod.Post, data);
                var responseObject = JObject.Parse(matrixResponse);
                var matrix = responseObject["matrix"]?.FirstOrDefault(m => m["resourceId"]?.ToString() == task.Session.AppId) ?? null;
                if (matrix != null)
                {
                    var privileges = responseObject["resources"][matrix["resourceId"]?.ToObject<string>()]["privileges"].ToList() ?? new List<JToken>();
                    var canAppRead = privileges?.FirstOrDefault(p => p?.ToObject<string>() == "read") ?? null;
                    if (canAppRead != null)
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The read rights of the user could not found.");
                return false;
            }
        }

        private JObject CreateTaskResult(ActiveTask task)
        {
            return JObject.FromObject(new
            {
                startTime = task.StartTime,
                status = task.Status,
                taskId = task.Id,
                appId = task.Session.AppId,
                userId = task.Session.User.ToString()
            });
        }

        private async Task<JArray> GetAllowedTasks(List<ActiveTask> activeTasks, DomainUser user, string appId)
        {
            var results = new JArray();
            try
            {
                var session = sessionManager.GetSession(onDemandConfig.Connection, user, appId);
                var qrsHub = new QlikQrsHub(session.ConnectUri, session.Cookie);
                foreach (var activeTask in activeTasks)
                {
                    if (activeTask.Stopped)
                        continue;

                    activeTask.Stoppable = false;
                    var appOwner = await GetAppOwner(qrsHub, activeTask.Session.AppId);
                    if (appOwner != null && appOwner.ToString() == activeTask.Session.User.ToString())
                    {
                        logger.Debug($"The app owner {appOwner.ToString()} for task list found.");
                        activeTask.Stoppable = true;
                        results.Add(CreateTaskResult(activeTask));
                    }
                    else
                    {
                        var readResult = await HasReadRights(qrsHub, activeTask);
                        if (readResult)
                        {
                            activeTask.Stoppable = true;
                            results.Add(CreateTaskResult(activeTask));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The list of tasks could not be determined.");
            }
            return results;
        }
        #endregion

        #region Public Methods
        public void Dispose() { }

        public override Task<Capabilities> GetCapabilities(Empty request, ServerCallContext context)
        {
            try
            {
                logger.Info($"GetCapabilities was called");

                return Task.FromResult(new Capabilities
                {
                    PluginVersion = onDemandConfig.AppVersion,
                    PluginIdentifier = onDemandConfig.AppName,
                    AllowScript = false,
                    Functions =
                    {
                        new FunctionDefinition
                        {
                            FunctionId = 1,
                            FunctionType = FunctionType.Scalar,
                            Name = nameof(SerFunction.START),
                            Params =
                            {
                                new Parameter { Name = "Script", DataType = DataType.String }
                            },
                            ReturnType = DataType.String
                        },
                        new FunctionDefinition
                        {
                             FunctionId = 2,
                             FunctionType = FunctionType.Scalar,
                             Name = nameof(SerFunction.STATUS),
                             Params =
                             {
                                new Parameter { Name = "Request", DataType = DataType.String },
                             },
                             ReturnType = DataType.String
                        },
                        new FunctionDefinition
                        {
                            FunctionId = 3,
                            FunctionType = FunctionType.Scalar,
                            Name = nameof(SerFunction.STOP),
                            Params =
                            {
                               new Parameter { Name = "Request", DataType = DataType.String },
                            },
                            ReturnType = DataType.String
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GetCapabilities has errors");
                return null;
            }
        }

        public override async Task ExecuteFunction(IAsyncStreamReader<BundledRows> requestStream,
                                                   IServerStreamWriter<BundledRows> responseStream,
                                                   ServerCallContext context)
        {
            try
            {
                logger.Debug("ExecuteFunction was called");
                Thread.Sleep(200);

                //Read function header
                var functionHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<FunctionRequestHeader>();

                //Read common header
                var commonHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<CommonRequestHeader>();

                //Set appid
                logger.Info($"Qlik AppId from header: {commonHeader?.AppId}");
                var activeAppId = commonHeader?.AppId;

                //Set qlik user
                logger.Debug($"Qlik DomainUser from header: {commonHeader?.UserId}");
                var domainUser = new DomainUser(commonHeader?.UserId);

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                var statusResult = new OnDemandResult() { Status = 0 };
                var row = GetParameter(requestStream);
                var json = GetParameterValue(0, row);

                var functionCall = (SerFunction)functionHeader.FunctionId;
                logger.Debug($"Function id: {functionCall}");

                //Check rest service health
                CheckRestService();

                //Caution: Personal//Me => Desktop Mode
                if (domainUser?.UserId == "sa_scheduler" && domainUser?.UserDirectory == "INTERNAL")
                {
                    try
                    {
                        logger.Debug($"Qlik Service user: {domainUser.ToString()}");
                        domainUser = new DomainUser("INTERNAL\\ser_scheduler");
                        logger.Debug($"Change to ser service user: {domainUser.ToString()}");
                        var tmpsession = sessionManager.GetSession(onDemandConfig.Connection, domainUser, activeAppId);
                        if (tmpsession == null)
                            throw new Exception("No session cookie generated. (Qlik Task)");
                        var qrsHub = new QlikQrsHub(onDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                        domainUser = await GetAppOwner(qrsHub, activeAppId);
                        if (domainUser == null)
                            throw new Exception("The owner of the could not found.");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Could not switch the task user to real qlik user.");
                    }
                }

                Guid? taskId = null;
                string versions = null;
                string tasks = null;
                string log = null;
                dynamic jsonObject;

                switch (functionCall)
                {
                    case SerFunction.START:
                        logger.Trace("Create report start");
                        statusResult = CreateReport(domainUser, activeAppId, json);
                        logger.Trace("Create report end");
                        break;
                    case SerFunction.STATUS:
                        #region Status
                        json = ConvertHJsonToJson(json);
                        if (json != null)
                        {
                            jsonObject = JObject.Parse(json);
                            taskId = jsonObject?.taskId ?? null;
                            versions = jsonObject?.versions ?? null;
                            tasks = jsonObject.tasks ?? null;
                            log = jsonObject.log ?? null;
                        }

                        if (versions == "all")
                        {
                            logger.Debug("Status - Read all versions.");
                            statusResult.Versions = onDemandConfig.PackageVersions;
                        }

                        if (tasks == "all")
                        {
                            logger.Debug("Status - Get all tasks.");
                            var activeTasks = runningTasks.Values?.ToArray().ToList() ?? new List<ActiveTask>();
                            statusResult.Tasks = GetAllowedTasks(activeTasks, domainUser, activeAppId).Result;
                        }
                        else if (taskId.HasValue)
                        {
                            logger.Debug($"Status - Get task status from id {taskId.Value}.");
                            var currentTask = runningTasks.ToArray().FirstOrDefault(t => t.Key == taskId.Value);
                            if (currentTask.Value != null)
                            {
                                statusResult.Distribute = currentTask.Value.Distribute;
                                statusResult.Status = currentTask.Value.Status;
                                statusResult.Log = currentTask.Value.Message;
                            }
                            else
                            {
                                logger.Debug($"Status - No task id in task {taskId.Value} pool found.");
                                statusResult.Status = 0;
                                statusResult.Log = "Ready";
                            }
                        }
                        else
                        {
                            logger.Trace("Status - No task with \"all\" or \"id\" found.");
                        }
                        break;
                    #endregion
                    case SerFunction.STOP:
                        #region Stop
                        json = ConvertHJsonToJson(json);
                        if (json != null)
                        {
                            jsonObject = JObject.Parse(json) ?? null;
                            taskId = jsonObject?.taskId ?? null;
                            tasks = jsonObject.tasks ?? null;
                        }

                        if (tasks == "all")
                        {
                            logger.Debug("Stop - All tasks.");
                            statusResult.Log = "Stop all tasks.";
                            if (runningTasks.Count == 0)
                                logger.Warn("No Tasks to stop");

                            foreach (var runningTask in runningTasks)
                            {
                                if (runningTask.Value?.Stoppable ?? false)
                                {
                                    runningTask.Value?.CancelSource?.Cancel();
                                    var stopResult = restClient.StopTasksAsync(runningTask.Value.Id).Result;
                                    if (stopResult.Success.Value)
                                    {
                                        runningTask.Value.Message = "The Task was stopped by user.";
                                        runningTask.Value.Status = 4;
                                        runningTask.Value.Stopped = true;
                                        FinishTask(runningTask.Value);
                                    }
                                    else
                                    {
                                        logger.Debug("All tasks could not stopped.");
                                        statusResult.Log = "All tasks could not stopped.";
                                    }
                                }
                            }
                        }
                        else if (taskId.HasValue)
                        {
                            var currentTask = runningTasks.ToArray().FirstOrDefault(t => t.Key == taskId.Value);
                            if (currentTask.Value != null)
                            {
                                currentTask.Value?.CancelSource?.Cancel();
                                var stopResult = restClient.StopTasksAsync(currentTask.Value.Id).Result;
                                if (stopResult.Success.Value)
                                    logger.Debug($"The task {currentTask.Value.Id} was stopped.");
                                else
                                    logger.Debug($"The task {currentTask.Value.Id} could not stopped.");
                                FinishTask(currentTask.Value);
                                statusResult.Log = $"Task {currentTask.Value?.Id} was stoppt.";
                                currentTask.Value.Stopped = true;
                                currentTask.Value.Status = 4;
                            }
                        }
                        statusResult.Status = 4;
                        break;
                    #endregion
                    default:
                        throw new Exception($"Unknown function id {functionHeader.FunctionId}.");
                }

                logger.Trace($"Qlik status result: {JsonConvert.SerializeObject(statusResult)}");
                await responseStream.WriteAsync(GetResult(statusResult));
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"ExecuteFunction - {ex.Message}");
                await responseStream.WriteAsync(GetResult(new OnDemandResult()
                {
                    Log = ex.Message,
                    Status = -1
                }));
            }
            finally
            {
                LogManager.Flush();
            }
        }
        #endregion

        #region Private Functions
        private OnDemandResult CreateReport(DomainUser qlikUser, string appId, string json)
        {
            ActiveTask activeTask = null;
            SessionInfo qlikSession = null;

            try
            {
                logger.Debug("Create Report");
                logger.Info($"Memory usage: {GC.GetTotalMemory(true)}");

                activeTask = new ActiveTask()
                {
                    Id = Guid.NewGuid(),
                    CancelSource = new CancellationTokenSource(),
                    Status = 1,
                    Session = qlikSession,
                    StartTime = DateTime.Now
                };

                MappedDiagnosticsLogicalContext.Set("jobId", activeTask.Id.ToString());
                logger.Info($"<user>{qlikUser.ToString()}</user>");

                //task to list
                runningTasks.TryAdd(activeTask.Id, activeTask);

                //get qlik session over jwt
                logger.Debug("Get qlik session over jwt.");
                qlikSession = sessionManager.GetSession(onDemandConfig.Connection, qlikUser, appId);
                if (qlikSession == null)
                    throw new Exception("No session cookie generated (check qmc settings or connector config).");

                //connect to qlik app
                logger.Debug("Connect to Qlik over websocket.");
                var fullConnectionConfig = new ConnectionConfig
                {
                    App = qlikSession.AppId,
                    ServerUri = onDemandConfig.Connection.ServerUri,
                    Credentials = new SerCredentials()
                    {
                        Type = QlikCredentialType.SESSION,
                        Key = qlikSession.Cookie.Name,
                        Value = qlikSession.Cookie.Value
                    }
                };
                var qlikConnection = ConnectionManager.NewConnection(fullConnectionConfig);
                if (qlikConnection == null)
                    throw new Exception("No Websocket connection to Qlik.");
                else
                {
                    qlikSession.QlikConn = qlikConnection;
                    activeTask.Session = qlikSession;
                }

                //create full engine config
                logger.Debug("Create ser engine full config.");
                var newEngineConfig = CreateEngineConfig(qlikSession, json);
                foreach (var configTask in newEngineConfig.Tasks)
                {
                    foreach (var configReport in configTask.Reports)
                    {
                        //Important: Add bearer connection as last connection item.
                        var firstConnection = configReport?.Connections?.FirstOrDefault() ?? null;
                        if (firstConnection != null)
                        {
                            logger.Debug("Create bearer connection.");
                            var newBearerConnection = CreateConnection(QlikCredentialType.HEADER, qlikSession, firstConnection.App);
                            configReport.Connections.Add(newBearerConnection);
                        }

                        //Read content from lib and content libary
                        logger.Debug("Get template data from qlik.");
                        var uploadsteam = FindTemplatePath(qlikSession, configReport.Template);
                        logger.Debug("Upload template data to rest service.");
                        var serfilename = Path.GetFileName(configReport.Template.Input);
                        var uploadResult = restClient.UploadAsync(serfilename, false, uploadsteam).Result;
                        if (uploadResult.Success.Value)
                        {
                            logger.Debug($"Upload {uploadResult.OperationId.ToString()} successfully.");
                            activeTask.FileUploadIds.Add(uploadResult.OperationId.Value);
                        }
                        else
                            logger.Warn($"The Upload was failed. - Error: {uploadResult?.Error}");
                        uploadsteam.Close();
                    }
                }

                //Append Upload ids on config
                var jobJson = JObject.FromObject(newEngineConfig);
                jobJson = AppendUploadGuids(jobJson, activeTask.FileUploadIds);
                activeTask.JobJson = jobJson;

                //Use the connector in the same App, than wait for data reload
                var scriptConnection = newEngineConfig?.Tasks?.SelectMany(s => s.Reports)?.SelectMany(r => r.Connections)?.FirstOrDefault(c => c.App == qlikSession.AppId) ?? null;
                Task.Run(() => WaitForDataLoad(activeTask, qlikSession, scriptConnection));
                return new OnDemandResult() { TaskId = activeTask.Id, Status = 1 };
            }
            catch (Exception ex)
            {
                if (activeTask != null)
                {
                    activeTask.Status = -1;
                    activeTask.Message = ex.Message;
                    FinishTask(activeTask);
                }

                if (qlikSession == null)
                {
                    activeTask.Status = -2;
                    activeTask.Message = ex.Message;
                }
                else
                    sessionManager.MakeSocketFree(activeTask?.Session ?? null);
                logger.Error(ex, "The report could not create.");
                return new OnDemandResult() { TaskId = activeTask?.Id, Status = activeTask?.Status ?? -1, Log = activeTask?.Message };
            }
        }

        private JObject AppendUploadGuids(JObject jobJson, List<Guid> guidList)
        {
            var jarray = new JArray();
            foreach (var guidItem in guidList)
                jarray.Add(guidItem.ToString());
            var uploadGuids = new JProperty("uploadGuids", jarray);
            jobJson.Last.AddAfterSelf(uploadGuids);
            return jobJson;
        }

        private void WaitForDataLoad(ActiveTask task, SessionInfo session, Ser.Api.SerConnection configConn)
        {
            var scriptApp = configConn?.App;
            var timeout = configConn?.RetryTimeout ?? 0;
            var dataloadCheck = ScriptCheck.DataLoadCheck(onDemandConfig.Connection.ServerUri, scriptApp, session, timeout);
            if (dataloadCheck)
            {
                logger.Debug("Start task on rest service.");
                var jobJson = task.JobJson.ToString();
                logger.Debug($"Script:{Environment.NewLine}{jobJson}");
                var createTaskResult = restClient.CreateTaskWithIdAsync(task.Id, jobJson).Result;
                if (createTaskResult.Success.Value)
                {
                    logger.Debug($"Task was started {createTaskResult?.OperationId}.");
                    var statusThread = new Thread(() => CheckStatus(task))
                    {
                        IsBackground = true
                    };
                    statusThread.Start();
                }
                else
                {
                    logger.Debug($"The task was failed - Error: {createTaskResult?.Error}.");
                    throw new Exception($"Task error: {createTaskResult?.Error}");
                }
            }
            else
                logger.Debug("Dataload check failed.");
        }

        private Stream FindTemplatePath(SessionInfo session, SerTemplate template)
        {
            var result = UriUtils.NormalizeUri(template.Input);
            var templateUri = result.Item1;
            if (templateUri.Scheme.ToLowerInvariant() == "content")
            {
                var contentFiles = GetLibraryContent(onDemandConfig.Connection.ServerUri, session.AppId, session.QlikConn.CurrentApp, result.Item2);
                logger.Debug($"File count in content library: {contentFiles?.Count}");
                var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(templateUri.AbsolutePath));
                if (filterFile != null)
                {
                    var data = DownloadFile(filterFile, session.Cookie);
                    template.Input = Path.GetFileName(filterFile);
                    return new MemoryStream(data);
                }
                else
                    throw new Exception($"No file in app library found.");
            }
            else if (templateUri.Scheme.ToLowerInvariant() == "lib")
            {
                var connUrl = session.QlikConn.CurrentApp.GetConnectionsAsync()
                    .ContinueWith<string>((connections) =>
                    {
                        var libResult = connections.Result.FirstOrDefault(n => n.qName.ToLowerInvariant() == result.Item2) ?? null;
                        if (libResult == null)
                            return null;
                        var libPath = libResult?.qConnectionString?.ToString();
                        var relPath = templateUri?.LocalPath?.TrimStart(new char[] { '\\', '/' })?.Replace("/", "\\");
                        if (relPath == null)
                            return null;
                        return $"{libPath}{relPath}";
                    }).Result;

                if (connUrl == null)
                    throw new Exception($"No path in content library found.");
                else
                {
                    template.Input = Path.GetFileName(connUrl);
                    return File.OpenRead(connUrl);
                }
            }
            else
            {
                throw new Exception($"Unknown Scheme in Filename Uri {template.Input}.");
            }
        }

        private byte[] DownloadFile(string relUrl, Cookie cookie)
        {
            using (var webClient = new WebClient())
            {
                webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
                return webClient.DownloadData($"{onDemandConfig.Connection.ServerUri.AbsoluteUri}{relUrl}");
            }
        }

        private SerConnection CreateConnection(QlikCredentialType type, SessionInfo session, string dataAppId = null)
        {
            try
            {
                logger.Debug("Create new connection.");
                var mainConnection = onDemandConfig.Connection;
                var token = sessionManager.GetToken(session.User, mainConnection, TimeSpan.FromMinutes(30));
                logger.Debug($"Bearer Token: {token}");

                var conn = new SerConnection()
                {
                    ServerUri = mainConnection.ServerUri,
                };

                switch (type)
                {
                    case QlikCredentialType.JWT:
                    case QlikCredentialType.HEADER:
                        conn.Credentials = new SerCredentials()
                        {
                            Type = type,
                            Key = "Authorization",
                            Value = $"Bearer { token }"
                        };
                        break;
                    case QlikCredentialType.SESSION:
                        conn.Credentials = new SerCredentials()
                        {
                            Type = type,
                            Key = session.Cookie?.Name ?? null,
                            Value = session.Cookie?.Value ?? null
                        };
                        break;
                    case QlikCredentialType.CERTIFICATE:
                        conn.Credentials = new SerCredentials()
                        {
                            Type = type,
                            Cert = onDemandConfig?.Connection?.Credentials?.Cert ?? null
                        };
                        break;
                    default:
                        logger.Error("Unknown connection type.");
                        break;
                }

                if (!String.IsNullOrEmpty(dataAppId))
                    conn.App = dataAppId;

                return conn;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The bearer connection could not create.");
                return null;
            }
        }

        private SerConfig CreateEngineConfig(SessionInfo session, string userJson)
        {
            logger.Trace($"Parse user script: {userJson}");
            userJson = userJson?.Trim();
            //Parse YAML
            logger.Trace("Parse YAML");
            var jsonStr = ConvertYamlToJson(userJson);
            if (jsonStr == null)
            {
                //Parse HJSON
                logger.Trace("Parse HJSON");
                jsonStr = ConvertHJsonToJson(userJson);
            }

            if (jsonStr == null)
                throw new Exception("Could not read user script.");

            //Make real JSON
            logger.Debug("Auto replacement to normal hjson structure.");
            if (!jsonStr.ToLowerInvariant().Contains("\"reports\":"))
                jsonStr = $"\"reports\":[{jsonStr}]";

            if (!jsonStr.ToLowerInvariant().Contains("\"tasks\":"))
                jsonStr = $"\"tasks\":[{{{jsonStr}}}]";

            if (!jsonStr.Trim().StartsWith("{"))
                jsonStr = $"{{{jsonStr}";

            if (!jsonStr.Trim().EndsWith("}"))
                jsonStr = $"{jsonStr}}}";

            dynamic serJsonConfig = JObject.Parse(jsonStr);

            logger.Debug("Search for connections.");
            var tasks = serJsonConfig?.tasks ?? new JArray();
            foreach (var task in tasks)
            {
                var reports = task?.reports ?? new JArray();
                foreach (var report in reports)
                {
                    var userConnections = new JArray();
                    JToken connections = report?.connections;
                    if (connections.Type == JTokenType.Object)
                        userConnections.Add(report?.connections);
                    else if (connections.Type == JTokenType.Array)
                        userConnections = report?.connections;
                    else
                        logger.Error("No valid connection type.");
                    var newUserConnections = new List<JToken>();
                    for (int i = 0; i < userConnections.Count; i++)
                    {
                        var mergeConnection = userConnections[i] as JObject;
                        var credType = onDemandConfig?.Connection?.Credentials?.Type ?? QlikCredentialType.JWT;
                        var connectorConnection = JObject.FromObject(CreateConnection(credType, session));
                        connectorConnection.Merge(mergeConnection, new JsonMergeSettings()
                        {
                            MergeNullValueHandling = MergeNullValueHandling.Ignore
                        });
                        newUserConnections.Add(connectorConnection);
                    }

                    report.connections = new JArray(newUserConnections);
                    JObject distribute = report.distribute;
                    var children = distribute?.Children().Children()?.ToList() ?? new List<JToken>();
                    foreach (dynamic child in children)
                    {
                        var connection = child.connections ?? null;
                        if (connection?.ToString() == "@CONFIGCONNECTION@")
                            child.connections = new JArray(newUserConnections);
                        var childProp = (child as JObject).Parent as JProperty;
                        if (childProp?.Name == "hub")
                        {
                            var hubOwner = child.owner ?? null;
                            if (hubOwner == null)
                                child.owner = session.User.ToString();
                        }
                    }

                    var privateKey = onDemandConfig?.Connection?.Credentials?.PrivateKey ?? null;
                    if (!String.IsNullOrEmpty(privateKey))
                    {
                        // For file access
                        lock (threadObject)
                        {
                            var path = SerUtilities.GetFullPathFromApp(privateKey);
                            var crypter = new TextCrypter(path);
                            var value = report?.template?.outputPassword ?? null;
                            if (value != null)
                            {
                                string password = value.Value<string>();
                                if (Convert.TryFromBase64String(password, new Span<byte>(), out var base64Result))
                                    report.template.outputPassword = crypter.DecryptText(password);
                                else
                                    report.template.outputPassword = password;
                            }
                        }
                    }
                }
            }

            //Resolve @PROPERTYNAME@ and @("
            var jsonResolver = new JsonConfigResolver(serJsonConfig.ToString());
            var jsonResult = jsonResolver.Resolve();
            var serConfiguration = JsonConvert.DeserializeObject<SerConfig>(jsonResult);
            return serConfiguration;
        }


        private List<string> GetLibraryContentInternal(IDoc app, string qName)
        {
            var libContent = app.GetLibraryContentAsync(qName).Result;
            return libContent.qItems.Select(u => u.qUrl).ToList();
        }

        private List<string> GetLibraryContent(Uri serverUri, string appId, IDoc app, string contentName = "")
        {
            try
            {
                var results = new List<string>();
                var readItems = new List<string>() { contentName };
                if (String.IsNullOrEmpty(contentName))
                {
                    // search for all App Specific ContentLibraries
                    var libs = app.GetContentLibrariesAsync().Result;
                    readItems = libs.qItems.Where(s => s.qAppSpecific == true).Select(s => s.qName).ToList();
                }

                foreach (var item in readItems)
                {
                    var qUrls = GetLibraryContentInternal(app, item);
                    results.AddRange(qUrls);
                }
                return results;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Could not read the content library.");
                return new List<string>();
            }
        }

        private string GetParameterValue(int index, Row row)
        {
            try
            {
                if (row == null || row?.Duals?.Count == 0 || index >= row?.Duals.Count)
                {
                    logger.Warn($"Parameter index {index} not found.");
                    return null;
                }

                return row.Duals[index].StrData;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Parameter index {index} not found with exception.");
                return null;
            }
        }

        private BundledRows GetResult(object result)
        {
            var resultBundle = new BundledRows();
            var resultRow = new Row();
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            };

            resultRow.Duals.Add(new Dual { StrData = JsonConvert.SerializeObject(result, settings), NumData = 0 });
            resultBundle.Rows.Add(resultRow);
            return resultBundle;
        }

        private byte[] GetStreamBuffer(Engine.Rest.Client.FileResponse result)
        {
            List<byte> bufferList = new List<byte>();
            var contentLength = result.Headers["Content-Length"].FirstOrDefault();
            if (contentLength != null)
            {
                var bufferLength = Convert.ToInt32(contentLength);
                var readLength = -1;
                while (readLength != 0)
                {
                    var buffer = new byte[bufferLength];
                    readLength = result.Stream.Read(buffer, 0, bufferLength);
                    bufferList.AddRange(buffer.Take(readLength));
                }
                return bufferList.ToArray();
            }
            else
            {
                var mem = new MemoryStream();
                result.Stream.CopyTo(mem);
                var buffer = mem?.GetBuffer() ?? null;
                bufferList.AddRange(buffer);
            }
            return bufferList.ToArray();
        }

        private void CheckStatus(ActiveTask task)
        {
            var cleanupPaths = new List<string>();

            try
            {
                var status = task.Status;
                var hasResult = false;
                var jobResults = new List<Ser.Engine.Rest.Client.JobResult>();
                task.Message = "Build Report, Please wait...";
                while (status != 2)
                {
                    logger.Trace("CheckStatus - Wait for finished tasks.");

                    Thread.Sleep(1000);
                    if (task.Status == -1 || task.Status == 0 || status == -1)
                        break;

                    if (status == 1)
                    {
                        var operationResult = restClient.TaskWithIdAsync(task.Id).Result;
                        if (operationResult.Success.Value)
                        {
                            jobResults = operationResult?.Results?.ToList() ?? new List<Ser.Engine.Rest.Client.JobResult>();
                            var runningResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.ABORT).ToList();
                            if (runningResults.Count == 0 && hasResult)
                            {
                                var finishResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.SUCCESS).ToList();
                                if (finishResults.Count == jobResults.Count)
                                    status = 2;
                                else
                                    status = -1;
                            }
                            else if (runningResults.Count > 0 || jobResults.Count(r => r.Status == Engine.Rest.Client.JobResultStatus.SUCCESS) == jobResults.Count)
                            {
                                hasResult = true;
                            }
                            else
                            {
                                var errorResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.ERROR).ToList();
                                if (errorResults.Count == jobResults.Count)
                                    status = -1;
                            }
                        }
                        else
                        {
                            logger.Warn("CheckStatus - The operation result of task response was false.");
                        }
                    }
                }

                //Status after build
                if (task.Status == 4)
                    throw new TaskCanceledException("The build of the report was canceled by user.");
                task.Status = status;
                if (status != 2)
                    throw new Exception("The report build process failed.");
                sessionManager.MakeSocketFree(task?.Session ?? null);

                //Download result files
                var distJobresults = ConvertApiType<List<JobResult>>(jobResults);
                foreach (var jobResult in distJobresults)
                {
                    var fileDataList = new List<JobResultFileData>();
                    foreach (var jobReport in jobResult.Reports)
                    {
                        foreach (var path in jobReport.Paths)
                        {
                            var filename = Path.GetFileName(path);
                            logger.Debug($"Download file {filename} form task {task.Id}.");
                            var streamData = restClient.DownloadFilesAsync(task.Id, filename).Result;
                            if (streamData != null)
                            {
                                var buffer = GetStreamBuffer(streamData);
                                var fileData = new JobResultFileData()
                                {
                                    Filename = filename,
                                    Data = buffer
                                };
                                fileDataList.Add(fileData);
                            }
                            else
                                logger.Warn($"File {filename} for download not found.");
                        }
                    }
                    jobResult.SetData(fileDataList);
                }

                //Delivery
                task.Message = "Delivery Report, Please wait...";
                status = StartDeliveryTool(task, distJobresults);
                task.Status = status;
                switch (status)
                {
                    case 3:
                        logger.Debug("The delivery was successfully.");
                        break;
                    case 4:
                        throw new TaskCanceledException("The delivery was canceled by user.");
                    default:
                        throw new Exception("The delivery process failed.");
                }
            }
            catch (TaskCanceledException ex)
            {
                task.Message = ex.Message;
                task.Status = 4;
                logger.Error(ex, "The status check was canceled by user.");
            }
            catch (Exception ex)
            {
                task.Message = ex.Message;
                task.Status = -1;
                logger.Error(ex, "The status check has detected a processing error.");
            }
            finally
            {
                //Cleanup
                sessionManager.MakeSocketFree(task?.Session ?? null);
                FinishTask(task, cleanupPaths);
                LogManager.Flush();
            }
        }

        private T ConvertApiType<T>(object value)
        {
            try
            {
                var json = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Convert type failed.");
                return default(T);
            }
        }

        private void FinishTask(ActiveTask task, List<string> cleanupPaths = null)
        {
            try
            {
                var finTask = Task
                .Delay(onDemandConfig.CleanupTimeout)
                .ContinueWith((_) =>
                {
                    logger.Debug($"Cleanup Process, Folder and Socket connection.");
                    if (runningTasks.TryRemove(task.Id, out var taskResult))
                        logger.Debug($"Remove task {task.Id} - Successfully.");
                    sessionManager.MakeSocketFree(task?.Session ?? null);
                    var deleteResult = restClient.DeleteFilesAsync(task.Id).Result;
                    if (deleteResult.Success.Value)
                        logger.Debug($"Delete task folder {task.Id} - Successfully.");
                    else
                        logger.Warn($"Delete task folder {task.Id} - Failed.");
                    foreach (var guidItem in task.FileUploadIds)
                    {
                        deleteResult = restClient.DeleteFilesAsync(guidItem).Result;
                        if (deleteResult.Success.Value)
                            logger.Debug($"Delete file folder {guidItem} - Successfully.");
                        else
                            logger.Warn($"Delete file folder {guidItem} - Failed.");
                    }

                    if (cleanupPaths != null)
                    {
                        foreach (var cleanupPath in cleanupPaths)
                        {
                            try
                            {
                                Directory.Delete(cleanupPath, true);
                            }
                            catch (Exception ex)
                            {
                                logger.Warn(ex, $"The folder {cleanupPath} could not remove.");
                            }
                        }
                    }

                    task.Status = 0;
                    logger.Debug($"Cleanup complete.");
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        private Row GetParameter(IAsyncStreamReader<BundledRows> requestStream)
        {
            try
            {
                if (requestStream.MoveNext().Result == false)
                    logger.Debug("The Request has no parameters.");

                return requestStream?.Current?.Rows?.FirstOrDefault() ?? null;
            }
            catch
            {
                return null;
            }
        }

        private int StartDeliveryTool(ActiveTask task, List<JobResult> jobResults)
        {
            try
            {
                var distribute = new Ser.Distribute.Distribute();
                var privateKeyPath = onDemandConfig.Connection.Credentials.PrivateKey;
                var privateKeyFullname = SerUtilities.GetFullPathFromApp(privateKeyPath);
                var result = distribute.Run(jobResults, privateKeyFullname, task.CancelSource.Token);
                var xmlResult = JsonConvert.DeserializeXNode(result, "distributeresult");
                if (task.CancelSource.IsCancellationRequested)
                {
                    logger.Debug("Distribute is canceled by user.");
                    return 4;
                }
                else if (result != null)
                {
                    logger.Debug($"Distribute result: {result}");
                    logger.Info($"{xmlResult}");
                    task.Distribute = result;
                    return 3;
                }
                else
                {
                    logger.Error("The distribute has errors.");
                    return -1;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The distribute was failed.");
                return -1;
            }
        }

        private int Status(Ser.Engine.Rest.Client.JobResult result)
        {
            logger.Trace($"The Report status is {result.Status}.");
            switch (result.Status)
            {
                case Engine.Rest.Client.JobResultStatus.ABORT:
                    return 1;
                case Engine.Rest.Client.JobResultStatus.SUCCESS:
                    return 2;
                case Engine.Rest.Client.JobResultStatus.ERROR:
                    logger.Error("No Report created (ERROR).");
                    return -1;
                case Engine.Rest.Client.JobResultStatus.WARNING:
                    logger.Warn("No successfully Report created (WARNING).");
                    return -1;
                default:
                    return 0;
            }
        }

        private string ConvertYamlToJson(string yaml)
        {
            try
            {
                if (String.IsNullOrEmpty(yaml))
                    return null;

                using (TextReader sr = new StringReader(yaml))
                {
                    var deserializer = new Deserializer();
                    var yamlConfig = deserializer.Deserialize(sr);
                    return JsonConvert.SerializeObject(yamlConfig);
                }
            }
            catch (Exception ex)
            {
                logger.Info(ex, "Could not normalize yaml, please check your script.");
                return null;
            }
        }

        private string ConvertHJsonToJson(string json)
        {
            try
            {
                if (!String.IsNullOrEmpty(json))
                    return HjsonValue.Parse(json).ToString();
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Could not normalize hjson, please check your script.");
                return null;
            }
        }
        #endregion
    }
}