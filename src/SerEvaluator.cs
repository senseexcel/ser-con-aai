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
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.IO;
    using Grpc.Core;
    using Qlik.Sse;
    using Google.Protobuf;
    using NLog;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Q2g.HelperQrs;
    using Ser.Api;
    using Hjson;
    using Ser.Distribute;
    using static Qlik.Sse.Connector;
    using System.Net.WebSockets;
    using Q2g.HelperPem;
    using Qlik.EngineAPI;
    using enigma;
    using ImpromptuInterface;
    using System.Reflection;
    using Ser.Engine.Rest;
    using System.Collections.Concurrent;
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
        private static bool CreateNewCookie = false;
        private static SerOnDemandConfig onDemandConfig;
        private SessionManager sessionManager;
        private Ser.Engine.Rest.Client.SerApiClient restClient;
        private ConcurrentBag<ActiveTask> runingTasks;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            onDemandConfig = config;
            restClient = new Ser.Engine.Rest.Client.SerApiClient(new HttpClient()) { BaseUrl = config.RestServiceUrl };
            sessionManager = new SessionManager();
            runingTasks = new ConcurrentBag<ActiveTask>();
            ValidationCallback.Connection = config.Connection;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += ValidationCallback.ValidateRemoteCertificate;
        }

        public void Dispose() { }
        #endregion

        #region Public functions
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
                var functionRequestHeaderStream = context.RequestHeaders.SingleOrDefault(header => header.Key == "qlik-functionrequestheader-bin");
                if (functionRequestHeaderStream == null)
                    throw new Exception("ExecuteFunction called without Function Request Header in Request Headers.");

                var functionRequestHeader = new FunctionRequestHeader();
                functionRequestHeader.MergeFrom(new CodedInputStream(functionRequestHeaderStream.ValueBytes));
                var commonHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<CommonRequestHeader>();

                //check rest service health
                try
                {
                    var healthResult = restClient.HealthStatusAsync().Result;
                    if (healthResult.Success.Value)
                        logger.Debug($"The status of rest service is {healthResult.Success} and the health is {healthResult.Health}.");
                    else
                        throw new Exception("The rest service is not available.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"No connection to rest service {onDemandConfig.RestServiceUrl}.", ex);
                }

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

                var functionCall = (SerFunction)functionRequestHeader.FunctionId;
                logger.Debug($"Function id: {functionCall}");

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
                        var qrshub = new QlikQrsHub(onDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                        var qrsResult = qrshub.SendRequestAsync($"app/{activeAppId}", HttpMethod.Get).Result;
                        logger.Trace($"appResult:{qrsResult}");
                        dynamic jObject = JObject.Parse(qrsResult);
                        string userDirectory = jObject?.owner?.userDirectory ?? null;
                        string userId = jObject?.owner?.userId ?? null;
                        if (String.IsNullOrEmpty(userDirectory) || String.IsNullOrEmpty(userId))
                            logger.Warn($"No user directory {userDirectory} or user id {userId} found.");
                        else
                        {
                            domainUser = new DomainUser($"{userDirectory}\\{userId}");
                            logger.Debug($"Use real Qlik User: {domainUser.ToString()}");
                        }
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
                        json = GetNormalizeJson(json);
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
                            var taskResult = restClient.TasksAsync().Result;
                            if (taskResult.Success.Value)
                            {
                                var allJobResults = taskResult?.Results?.ToList() ?? new List<Engine.Rest.Client.JobResult>();
                                statusResult.Tasks.AddRange(allJobResults);
                            }
                        }
                        else if (taskId.HasValue)
                        {
                            logger.Debug($"Status - Get task status from id {taskId.Value}.");
                            var currentTask = runingTasks.ToArray().FirstOrDefault(t => t.Id == taskId.Value) ?? null;
                            if (currentTask != null)
                            {
                                statusResult.Status = currentTask.Status;
                                statusResult.Log = currentTask.Message;
                                statusResult.Distribute = currentTask.Distribute;
                            }
                            else
                                logger.Warn($"Status - No task id in task {taskId.Value} pool found.");
                        }
                        else
                        {
                            logger.Warn("Status - No task id found.");
                        }
                        break;
                    #endregion
                    case SerFunction.STOP:
                        #region Stop
                        json = GetNormalizeJson(json);
                        if (json != null)
                        {
                            jsonObject = JObject.Parse(json) ?? null;
                            taskId = jsonObject?.taskId ?? null;
                            tasks = jsonObject.tasks ?? null;
                        }

                        if (tasks == "all")
                        {
                            logger.Debug("Stop - All tasks.");
                            var stopResult = restClient.StopAllTasksAsync().Result;
                            if (stopResult.Success.Value)
                                logger.Debug("All tasks stopped.");
                            else
                                logger.Debug("All tasks could not stopped.");
                        }
                        else if (taskId.HasValue)
                        {
                            var currentTask = runingTasks.ToArray().FirstOrDefault(t => t.Id == taskId.Value);
                            if (currentTask == null)
                            {
                                var stopResult = restClient.StopTasksAsync(currentTask.Id).Result;
                                if (stopResult.Success.Value)
                                    logger.Debug($"The task {currentTask.Id} was stopped.");
                                else
                                    logger.Debug($"The task {currentTask.Id} could not stopped.");
                                currentTask.Status = 0;
                                FinishTask(currentTask);
                            }
                        }
                        break;
                    #endregion
                    default:
                        throw new Exception($"Unknown function id {functionRequestHeader.FunctionId}.");
                }

                logger.Debug($"Qlik status result: {JsonConvert.SerializeObject(statusResult)}");
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
                logger.Info($"Memory usage: {GC.GetTotalMemory(true)}");

                //get qlik session over jwt
                logger.Debug("Get qlik session over jwt.");
                qlikSession = sessionManager.GetSession(onDemandConfig.Connection, qlikUser, appId, CreateNewCookie);
                if (qlikSession == null)
                    throw new Exception("No session cookie generated (check qmc settings or connector config).");

                //connect to qlik app
                logger.Debug("Connect to Qlik over websocket.");
                var qlikApp = QlikWebsocket.CreateNewConnection(qlikSession);
                if (qlikApp == null)
                    throw new Exception("No Websocket connection to Qlik.");

                CreateNewCookie = false;
                activeTask = new ActiveTask()
                {
                    Status = 1,
                    Session = qlikSession,
                    StartTime = DateTime.Now,
                };
                runingTasks.Add(activeTask);
                //create full engine config
                logger.Debug("Create ser engine full config");
                var newEngineConfig = GetNewJson(qlikSession, qlikApp, json);
                foreach (var configTask in newEngineConfig.Tasks)
                {
                    foreach (var configReport in configTask.Reports)
                    {
                        //Read content from lib and content libary
                        logger.Debug("Get template data from qlik.");
                        var uploadsteam = FindTemplatePath(qlikSession, qlikApp, configReport.Template);
                        logger.Debug("Upload template data to rest service.");
                        var serfilename = Path.GetFileName(configReport.Template.Input);
                        var uploadResult = restClient.UploadWithIdAsync(activeTask.Id, serfilename, false, uploadsteam).Result;
                        if (uploadResult.Success.Value)
                        {
                            logger.Debug($"Upload {uploadResult.OperationId.ToString()} successfully.");
                            activeTask.FileUploadIds.Add(uploadResult.OperationId.Value);
                        }
                        else
                            logger.Warn($"The Upload was failed. - Error: {uploadResult?.Error}");
                        uploadsteam.Close();
                        activeTask.TaksCount++;
                    }
                }

                //Append Upload ids on config
                var jobJson = JObject.FromObject(newEngineConfig);
                jobJson = AppendUploadGuids(jobJson, activeTask.FileUploadIds);
                activeTask.JobJson = jobJson;

                //Use the connector in the same App, than wait for data reload
                var scriptConnection = newEngineConfig?.Tasks?.SelectMany(s => s.Reports)?.SelectMany(r => r.Connections)?.FirstOrDefault(c => c.App == qlikSession.AppId) ?? null;
                Task.Run(() => WaitForDataLoad(activeTask, qlikSession, qlikApp, scriptConnection));
                return new OnDemandResult() { TaskId = activeTask.Id, Status = 1 };
            }
            catch (Exception ex)
            {
                if (activeTask != null)
                    activeTask.Status = -1;
                if (qlikSession == null)
                    activeTask.Status = -2;
                else
                {
                    qlikSession.SocketSession?.CloseAsync()?.Wait(500);
                    qlikSession.SocketSession = null;
                }
                activeTask.Message = ex.Message;
                CreateNewCookie = true;
                logger.Error(ex, "The report could not create.");
                FinishTask(activeTask);
                return new OnDemandResult() { TaskId = activeTask?.Id, Status = activeTask.Status, Log = activeTask?.Message };
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

        private void WaitForDataLoad(ActiveTask task, SessionInfo session, IDoc qlikApp, Ser.Api.SerConnection configConn)
        {
            var scriptApp = configConn?.App;
            var timeout = configConn?.RetryTimeout ?? 0;
            var dataloadCheck = ScriptCheck.DataLoadCheck(onDemandConfig.Connection.ServerUri, scriptApp, session, timeout);
            if (dataloadCheck)
            {
                logger.Debug("Start task on rest service.");
                var createTaskResult = restClient.CreateTaskAsync(task.JobJson.ToString()).Result;
                if (createTaskResult.Success.Value)
                {
                    logger.Debug($"Task was started {createTaskResult?.OperationId}.");
                    task.Id = createTaskResult.OperationId.Value;
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

        private Process GetProcess(int id)
        {
            try
            {
                return Process.GetProcesses()?.FirstOrDefault(p => p.Id == id) ?? null;
            }
            catch
            {
                return null;
            }
        }

        private Stream FindTemplatePath(SessionInfo session, IDoc qlikApp, SerTemplate template)
        {
            var result = UriUtils.NormalizeUri(template.Input);
            var templateUri = result.Item1;
            if (templateUri.Scheme.ToLowerInvariant() == "content")
            {
                var contentFiles = GetLibraryContent(onDemandConfig.Connection.ServerUri, session.AppId, qlikApp, result.Item2);
                logger.Debug($"File count in content library: {contentFiles?.Count}");
                var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(templateUri.AbsolutePath));
                if (filterFile != null)
                {
                    var data = DownloadFile(filterFile, session.Cookie);
                    template.Input = Path.GetFileName(filterFile);
                    return new MemoryStream(data);
                }
                else
                    throw new Exception($"No file in content library found.");
            }
            else if (templateUri.Scheme.ToLowerInvariant() == "lib")
            {
                var connUrl = qlikApp.GetConnectionsAsync()
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
                    throw new Exception($"No path in connection library found.");
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
            var webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
            return webClient.DownloadData($"{onDemandConfig.Connection.ServerUri.AbsoluteUri}{relUrl}");
        }

        private SerConfig GetNewJson(SessionInfo session, IDoc qlikApp, string userJson)
        {
            //Bearer Connection
            var mainConnection = onDemandConfig.Connection;
            logger.Debug("Create Bearer Connection");
            var token = sessionManager.GetToken(session.User, mainConnection, TimeSpan.FromMinutes(30));
            logger.Debug($"Token: {token}");
            var privateKey = String.Empty;

            if (mainConnection.Credentials != null)
            {
                var cred = mainConnection.Credentials;
                cred.Type = QlikCredentialType.SESSION;
                cred.Value = session.Cookie.Value;
                privateKey = cred.PrivateKey;
            }

            if (!userJson.ToLowerInvariant().Contains("reports:") &&
                !userJson.ToLowerInvariant().Contains("\"reports\":"))
                userJson = $"reports:[{{{userJson}}}]";

            if (!userJson.ToLowerInvariant().Contains("tasks:") &&
                !userJson.ToLowerInvariant().Contains("\"tasks\":"))
                userJson = $"tasks:[{{{userJson}}}]";

            if (!userJson.Trim().StartsWith("{"))
                userJson = $"{{{userJson}";
            if (!userJson.Trim().EndsWith("}"))
                userJson = $"{userJson}}}";

            logger.Trace($"Parse user json: {userJson}");
            var jsonConfig = GetNormalizeJson(userJson);
            var serConfig = JObject.Parse(jsonConfig);

            dynamic configConnection = JObject.Parse(JsonConvert.SerializeObject(mainConnection, Formatting.Indented));
            configConnection.credentials.cert = null;
            configConnection.credentials.privateKey = null;

            logger.Debug("search for connections.");
            var tasks = serConfig["tasks"]?.ToList() ?? null;
            foreach (var task in tasks)
            {
                var reports = task["reports"]?.ToList() ?? null;
                foreach (var report in reports)
                {
                    var userConnections = report["connections"].ToList();
                    var newUserConnections = new List<JToken>();
                    var currentApp = String.Empty;
                    foreach (var userConnection in userConnections)
                    {
                        var cvalue = userConnection.ToString();
                        if (!cvalue.StartsWith("{"))
                            cvalue = $"{{{cvalue}}}";
                        var currentConnection = JObject.Parse(cvalue);
                        //merge connections / config <> script
                        currentConnection.Merge(configConnection);
                        newUserConnections.Add(currentConnection);
                        currentApp = (currentConnection as dynamic)?.app ?? null;
                    }
                    //Important: The bearer connection must be added as last.
                    var bearerSerConnection = new Ser.Api.SerConnection()
                    {
                        App = currentApp,
                        ServerUri = onDemandConfig.Connection.ServerUri,
                        Credentials = new Ser.Api.SerCredentials()
                        {
                            Type = QlikCredentialType.HEADER,
                            Key = "Authorization",
                            Value = $"Bearer { token }",
                        },
                    };
                    var bearerConnection = JToken.Parse(JsonConvert.SerializeObject(bearerSerConnection, Formatting.Indented));
                    logger.Debug("serialize config connection.");
                    newUserConnections.Add(bearerConnection);

                    report["connections"] = new JArray(newUserConnections);
                    var distribute = report["distribute"];
                    var children = distribute?.Children().Children()?.ToList() ?? new List<JToken>();
                    foreach (var child in children)
                    {
                        var connection = child["connections"] ?? null;
                        if (connection?.ToString() == "@CONFIGCONNECTION@")
                            child["connections"] = newUserConnections.FirstOrDefault();
                        var childProp = child.Parent as JProperty;
                        if (childProp?.Name == "hub")
                        {
                            var hubOwner = child["owner"] ?? null;
                            if (hubOwner == null)
                                child["owner"] = session.User.ToString();
                        }
                    }

                    if (!String.IsNullOrEmpty(privateKey))
                    {
                        var path = SerUtilities.GetFullPathFromApp(privateKey);
                        var crypter = new TextCrypter(path);
                        var value = report["template"]["outputPassword"] ?? null;
                        if (value != null)
                        {
                            var password = value.Value<string>();
                            if (Convert.TryFromBase64String(password, new Span<byte>(), out var base64Result))
                                report["template"]["outputPassword"] = crypter.DecryptText(password);
                            else
                                report["template"]["outputPassword"] = password;
                        }
                    }
                }
            }

            var qlikResolver = new QlikResolver(qlikApp);
            serConfig = qlikResolver.Resolve(serConfig);
            var result = JsonConvert.DeserializeObject<SerConfig>(serConfig.ToString());
            return result;
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

        private void CheckStatus(ActiveTask task)
        {
            try
            {
                var status = task.Status;
                var jobresults = new List<Ser.Engine.Rest.Client.JobResult>();
                task.Message = "Build Report, Please wait...";
                while (status != 2)
                {
                    logger.Trace("CheckStatus - Wait for finished tasks.");

                    Thread.Sleep(1000);
                    if (task.Status == -1 || task.Status == 0)
                        break;

                    if (status == 1)
                    {
                        var operationResult = restClient.TaskWithIdAsync(task.Id).Result;
                        if (operationResult.Success.Value)
                        {
                            jobresults = operationResult?.Results?.ToList() ?? new List<Ser.Engine.Rest.Client.JobResult>();
                            var finishedResults = jobresults.Where(r => r.Status != Engine.Rest.Client.JobResultStatus.ABORT).ToList();
                            if (finishedResults.Count == task.TaksCount)
                                status = 2;
                        }
                        else
                            logger.Warn("CheckStatus - The operation result of task response was false.");
                    }
                }

                //Generate Reports
                task.Status = status;
                if (status != 2)
                    throw new Exception("The report build process failed.");
                task.Session.SocketSession?.CloseAsync()?.Wait(500);
                task.Session.SocketSession = null;
                task.Message = "Delivery Report, Please wait...";

                //Delivery
                //ODER wenn das nicht geht - Als json serialisieren und deserialisieren!!!
                var serJobresults = (List<JobResult>)Convert.ChangeType(jobresults, typeof(List<JobResult>));
                status = StartDeliveryTool(task, serJobresults);
                task.Status = status;
                if (status != 3)
                    throw new Exception("The delivery process failed.");
            }
            catch (Exception ex)
            {
                if (task?.Session?.SocketSession != null)
                {
                    task.Session.SocketSession?.CloseAsync()?.Wait(500);
                    task.Session.SocketSession = null;
                }
                task.Message = ex.Message;
                logger.Error(ex, "The status check has detected a processing error.");
            }
            finally
            {
                //Cleanup
                FinishTask(task);
            }
        }

        private void FinishTask(ActiveTask task)
        {
            try
            {
                var finTask = Task
                .Delay(onDemandConfig.CleanupTimeout)
                .ContinueWith((_) =>
                {
                    logger.Debug($"Cleanup Process, Folder and Socket connection.");
                    if (task.Session.SocketSession != null)
                    {
                        task.Session.SocketSession?.CloseAsync()?.Wait(250);
                        task.Session.SocketSession = null;
                    }
                    restClient.DeleteFilesAsync(task.Id);
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
                var result = distribute.Run(jobResults, privateKeyFullname);
                task.Distribute = result;
                return 3;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The delivery tool could not start as process.");
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

        private string GetNormalizeJson(string json)
        {
            try
            {
                if (!String.IsNullOrEmpty(json))
                    return HjsonValue.Parse(json).ToString();
                return null;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        #endregion
    }
}