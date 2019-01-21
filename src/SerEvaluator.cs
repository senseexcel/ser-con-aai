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
        private TaskManager taskManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            onDemandConfig = config;
            taskManager = new TaskManager();
            ValidationCallback.Connection = config.Connection;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += ValidationCallback.ValidateRemoteCertificate;
            ////RestoreTasks();
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
                logger.Info($"request from user: {commonHeader.UserId} for AppId: {commonHeader.AppId}");
                var domainUser = new DomainUser(commonHeader.UserId);
                logger.Debug($"DomainUser: {domainUser.ToString()}");

                var userParameter = new UserParameter()
                {
                    AppId = commonHeader.AppId,
                    DomainUser = domainUser,
                    WorkDir = PathUtils.GetFullPathFromApp(onDemandConfig.WorkingDir),
                };

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                var result = new OnDemandResult() { Status = 0 };
                var row = GetParameter(requestStream);
                var json = GetParameterValue(0, row);

                var functionCall = (SerFunction)functionRequestHeader.FunctionId;
                logger.Debug($"Function id: {functionCall}");
                dynamic jsonObject;

                //Caution: Personal//Me => Desktop Mode
                ActiveTask activeTask = null;
                if (userParameter.DomainUser.UserId == "sa_scheduler" &&
                    userParameter.DomainUser.UserDirectory == "INTERNAL")
                {
                    try
                    {
                        //In Doku mit aufnehmen / Security rule für Task User ser_scheduler
                        userParameter.DomainUser = new DomainUser("INTERNAL\\ser_scheduler");
                        activeTask = taskManager.CreateTask(userParameter);
                        logger.Debug($"scheduler task: {activeTask.Id}");
                        var tmpsession = taskManager.GetSession(onDemandConfig.Connection, activeTask);
                        var qrshub = new QlikQrsHub(onDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                        var qrsResult = qrshub.SendRequestAsync($"app/{userParameter.AppId}", HttpMethod.Get).Result;
                        logger.Trace($"appResult:{qrsResult}");
                        dynamic jObject = JObject.Parse(qrsResult);
                        string userDirectory = jObject?.owner?.userDirectory ?? null;
                        string userId = jObject?.owner?.userId ?? null;
                        if (String.IsNullOrEmpty(userDirectory) || String.IsNullOrEmpty(userId))
                            logger.Warn($"No user directory {userDirectory} or user id {userId} found.");
                        else
                        {
                            userParameter.DomainUser = new DomainUser($"{userDirectory}\\{userId}");
                            logger.Debug($"New DomainUser: {userParameter.DomainUser.ToString()}");
                        }

                        lock (this)
                        {
                            taskManager.RemoveTask(activeTask.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "could not switch the task user.");
                    }
                }

                string taskId = null;
                string versions = null;
                string tasks = null;
                string log = null;

                switch (functionCall)
                {
                    case SerFunction.START:
                        logger.Trace("Create report start");
                        result = CreateReport(userParameter, json);
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

                        var statusResult = new OnDemandResult()
                        {
                            Status = 0,
                        };

                        if (versions == "all")
                            statusResult.Versions = onDemandConfig.PackageVersions;

                        if (!String.IsNullOrEmpty(taskId))
                        {
                            activeTask = taskManager.GetRunningTask(taskId);
                            if (activeTask != null)
                            {
                                if (tasks == "all")
                                {
                                    var session = activeTask.Session;
                                    statusResult.Tasks = taskManager.GetAllTasksForUser(session?.ConnectUri,
                                                                                        session?.Cookie, activeTask.UserId);
                                }
                                statusResult.Status = activeTask.Status;
                                statusResult.Log = activeTask.Message;
                                statusResult.Distribute = activeTask.Distribute;
                            }
                            else
                                logger.Debug($"No existing task id {taskId} found.");
                        }
                        result = statusResult;
                        break;
                    #endregion
                    case SerFunction.STOP:
                        #region Stop
                        json = GetNormalizeJson(json);
                        if (json != null)
                        {
                            jsonObject = JObject.Parse(json) ?? null;
                            taskId = jsonObject?.taskId ?? null;
                        }
                        activeTask = taskManager.GetRunningTask(taskId);
                        if (activeTask == null)
                        {
                            //Alle Prozesse zur AppId beenden
                            logger.Debug($"Stop all processes for app id {userParameter.AppId}");
                        }
                        else
                        {
                            activeTask.Status = 0;
                            FinishTask(userParameter, activeTask);
                        }

                        result = new OnDemandResult() { Status = activeTask.Status };
                        break;
                    #endregion
                    default:
                        throw new Exception($"Unknown function id {functionRequestHeader.FunctionId}.");
                }

                logger.Debug($"Result: {result}");
                await responseStream.WriteAsync(GetResult(result));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ExecuteFunction has errors");
                await responseStream.WriteAsync(GetResult(new OnDemandResult()
                {
                    Log = ex.ToString(),
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
        private OnDemandResult CreateReport(UserParameter parameter, string json)
        {
            ActiveTask activeTask = null;
            var currentWorkingDir = String.Empty;

            try
            {
                var id = new DirectoryInfo(parameter.WorkDir).Name;
                if (!Guid.TryParse(id, out var result))
                {
                    if (activeTask == null)
                        activeTask = taskManager.CreateTask(parameter);

                    currentWorkingDir = Path.Combine(parameter.WorkDir, activeTask.Id);
                    Directory.CreateDirectory(currentWorkingDir);
                    logger.Debug($"New Task-ID: {activeTask.Id}");
                }
                else
                {
                    activeTask = taskManager.GetRunningTask(id);
                    logger.Debug($"Running Task-ID: {activeTask.Id}");
                    currentWorkingDir = parameter.WorkDir;
                }

                logger.Debug($"TempFolder: {currentWorkingDir}");
                activeTask.WorkingDir = currentWorkingDir;

                //check for task is already running
                var task = taskManager.GetRunningTask(activeTask.Id);
                if (task != null)
                {
                    if (task.Status == 1 || task.Status == 2)
                    {
                        logger.Debug("Session ist already running.");
                        return new OnDemandResult() { TaskId = activeTask.Id };
                    }
                }

                //get a session and websocket connection
                var session = taskManager.GetSession(onDemandConfig.Connection, activeTask, CreateNewCookie);
                if (session == null)
                {
                    SoftDelete(currentWorkingDir);
                    Program.Service?.CheckQlikConnection();
                    throw new Exception("No session cookie generated.");
                }

                parameter.ConnectCookie = session?.Cookie;
                parameter.SocketConnection = QlikWebsocket.CreateNewConnection(session);
                if (parameter.SocketConnection == null)
                    throw new Exception("No Websocket connection to Qlik.");

                CreateNewCookie = false;
                activeTask.Status = 1;

                //get engine config
                var newEngineConfig = GetNewJson(parameter, json, currentWorkingDir);
                parameter.CleanupTimeout = newEngineConfig.Tasks.FirstOrDefault()
                                           .Reports.FirstOrDefault().General.CleanupTimeOut * 1000;

                //Save template from content libary
                FindTemplatePaths(parameter, newEngineConfig, currentWorkingDir);

                //Save config for SER engine
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                var settings = new JsonSerializerSettings()
                {
                    DefaultValueHandling = DefaultValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                };
                var serConfig = JsonConvert.SerializeObject(newEngineConfig, settings);
                File.WriteAllText(savePath, serConfig, Encoding.UTF8);

                //Use the connector in the same App, than wait for data reload
                var scriptConnection = newEngineConfig?.Tasks?.SelectMany(s => s.Reports)?.SelectMany(r => r.Connections)?.FirstOrDefault(c => c.App == parameter.AppId) ?? null;
                Task.Run(() => WaitForDataLoad(activeTask, parameter, session, scriptConnection?.App, scriptConnection?.RetryTimeout ?? 0));
                return new OnDemandResult() { TaskId = activeTask.Id, Status = 1 };
            }
            catch (Exception ex)
            {
                var task = taskManager.GetRunningTask(activeTask.Id);
                if (task != null)
                    task.Status = -1;
                if (task.Session == null)
                    task.Status = -2;
                task.Message = ex.Message;
                CreateNewCookie = true;
                task.Session.SocketSession?.CloseAsync()?.Wait(500);
                task.Session.SocketSession = null;
                logger.Error(ex, "The report could not create.");
                FinishTask(parameter, task);
                return new OnDemandResult() { TaskId = activeTask.Id, Status = task.Status, Log = task.Message };
            }
        }

        private void StartProcess(ActiveTask task, UserParameter parameter)
        {
            //Start SER Engine as Process
            var currentWorkDir = Path.Combine(parameter.WorkDir, task.Id);
            var enginePath = PathUtils.GetFullPathFromApp(onDemandConfig.SerEnginePath);
            logger.Debug($"Start Engine \"{enginePath}\"...");
            var serProcess = new Process();
            serProcess.StartInfo.FileName = "dotnet";
            serProcess.StartInfo.Arguments = $"{enginePath} --workdir \"{currentWorkDir}\"";
            serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
            serProcess.Start();
            task.ProcessId = serProcess.Id;

            //Write process file
            var procFileName = $"{Path.GetFileNameWithoutExtension(serProcess.StartInfo.FileName)}.pid";
            File.WriteAllText(Path.Combine(currentWorkDir, procFileName),
                              $"{serProcess.Id.ToString()}|{parameter.AppId.ToString()}|{parameter.DomainUser.ToString()}");

            var statusThread = new Thread(() => CheckStatus(parameter, task))
            {
                IsBackground = true
            };
            statusThread.Start();
        }

        private void WaitForDataLoad(ActiveTask task, UserParameter parameter, SessionInfo session, string scriptApp, int timeout)
        {
            var dataloadCheck = ScriptCheck.DataLoadCheck(onDemandConfig.Connection.ServerUri, scriptApp, parameter, session, timeout);
            if (dataloadCheck)
            {
                logger.Debug("Start the engine.");
                StartProcess(task, parameter);
            }
            else
                logger.Debug("Dataload failed.");
        }

        private bool KillProcess(int id)
        {
            try
            {
                var process = GetProcess(id);
                if (process != null)
                {
                    logger.Debug($"Stop Process with ID: {process?.Id} and Name: {process?.ProcessName}");
                    process?.Kill();
                    Thread.Sleep(1000);
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
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

        private void RestoreTasks()
        {
            logger.Debug("Check for reload tasks");
            var tempFolder = PathUtils.GetFullPathFromApp(onDemandConfig.WorkingDir);
            var tempFolders = Directory.GetDirectories(tempFolder);
            foreach (var folder in tempFolders)
            {
                var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    //find pid
                    var pidFile = files.Where(f => f.EndsWith(".pid")).FirstOrDefault();
                    if (pidFile != null)
                    {
                        var contentArray = File.ReadAllText(pidFile).Trim().Split('|');
                        if (contentArray.Length == 3)
                        {
                            logger.Debug($"Reload Task from folder {folder}");
                            var pId = Convert.ToInt32(contentArray[0]);
                            var parameter = new UserParameter()
                            {
                                AppId = contentArray[1],
                                DomainUser = new DomainUser(contentArray[2]),
                                WorkDir = folder,
                            };

                            var jobJsonFile = files.Where(f => f.EndsWith("userJson.hjson")).FirstOrDefault();
                            var json = File.ReadAllText(jobJsonFile);

                            //Remove Result file for distribute
                            var resultFile = files.Where(f => f.Contains("\\result_") && f.EndsWith(".json")).FirstOrDefault() ?? null;
                            if (resultFile != null)
                                SoftDelete(Path.GetDirectoryName(resultFile));

                            CreateReport(parameter, json);
                        }
                        else
                        {
                            logger.Debug($"The content of the process file {pidFile} is invalid.");
                        }
                    }
                    else
                    {
                        logger.Debug($"No process file in folder {folder} found.");
                    }
                }
                else
                {
                    Directory.Delete(folder);
                }
            }
        }

        private void FindTemplatePaths(UserParameter parameter, SerConfig config, string workDir)
        {
            var cookie = parameter.ConnectCookie;
            foreach (var task in config.Tasks)
            {
                var report = task.Reports.FirstOrDefault() ?? null;
                var result = UriUtils.NormalizeUri(report.Template.Input);
                var templateUri = result.Item1;
                if (templateUri.Scheme.ToLowerInvariant() == "content")
                {
                    var contentFiles = GetLibraryContent(onDemandConfig.Connection.ServerUri, parameter.AppId, parameter.SocketConnection, result.Item2);
                    logger.Debug($"File count in content library: {contentFiles?.Count}");
                    var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(templateUri.AbsolutePath));
                    if (filterFile != null)
                    {
                        var savePath = DownloadFile(filterFile, workDir, cookie);
                        report.Template.Input = Path.GetFileName(savePath);
                    }
                    else
                        throw new Exception($"No file in content library found.");
                }
                else if (templateUri.Scheme.ToLowerInvariant() == "lib")
                {
                    var connUrl = parameter.SocketConnection.GetConnectionsAsync()
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
                        report.Template.Input = connUrl;
                }
                else
                {
                    throw new Exception($"Unknown Scheme in Filename Uri {report.Template.Input}.");
                }
            }
        }

        private string DownloadFile(string relUrl, string workdir, Cookie cookie)
        {
            var savePath = Path.Combine(workdir, Path.GetFileName(relUrl));
            var webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
            webClient.DownloadFile($"{onDemandConfig.Connection.ServerUri.AbsoluteUri}{relUrl}", savePath);
            return savePath;
        }

        private SerConfig GetNewJson(UserParameter parameter, string userJson, string workdir)
        {
            //Bearer Connection
            var mainConnection = onDemandConfig.Connection;
            logger.Debug("Create Bearer Connection");
            var token = taskManager.GetToken(parameter.DomainUser, mainConnection, TimeSpan.FromMinutes(30));
            logger.Debug($"Token: {token}");

            if (mainConnection.Credentials != null)
            {
                var cred = mainConnection.Credentials;
                cred.Type = QlikCredentialType.SESSION;
                cred.Value = parameter.ConnectCookie.Value;
                parameter.PrivateKeyPath = cred.PrivateKey;
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
                    var bearerSerConnection = new SerConnection()
                    {
                        App = currentApp,
                        ServerUri = onDemandConfig.Connection.ServerUri,
                        Credentials = new SerCredentials()
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
                                child["owner"] = parameter.DomainUser.ToString();
                        }
                    }

                    if (!String.IsNullOrEmpty(parameter.PrivateKeyPath))
                    {
                        var path = PathUtils.GetFullPathFromApp(parameter.PrivateKeyPath);
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

            var qlikResolver = new QlikResolver(parameter.SocketConnection);
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

        private void CheckStatus(UserParameter parameter, ActiveTask task)
        {
            try
            {
                var status = 0;
                task.Message = "Build Report, Please wait...";
                while (status != 2)
                {
                    Thread.Sleep(250);
                    if (task.Status == -1 || task.Status == 0)
                    {
                        status = task.Status;
                        break;
                    }
                    status = Status(Path.Combine(parameter.WorkDir, task.Id), task.Status);
                    if (status == -1 || status == 0)
                        break;

                    if (status == 1)
                    {
                        var serProcess = GetProcess(task.ProcessId);
                        if (serProcess == null)
                        {
                            status = -1;
                            logger.Error("The engine process was terminated.");
                            break;
                        }
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
                status = StartDeliveryTool(task, parameter);
                task.Status = status;
                if (status != 3)
                    throw new Exception("The delivery process failed.");
            }
            catch (Exception ex)
            {
                task.Message = ex.Message;
                logger.Error(ex, "The status check has detected a processing error.");
            }
            finally
            {
                //Cleanup
                FinishTask(parameter, task);
            }
        }

        private void FinishTask(UserParameter parameter, ActiveTask task)
        {
            try
            {
                var finTask = Task
                .Delay(parameter.CleanupTimeout)
                .ContinueWith((_) =>
                {
                    logger.Debug($"Cleanup Process, Folder and Task");
                    if (task.Session.SocketSession != null)
                    {
                        task.Session.SocketSession?.CloseAsync()?.Wait(250);
                        task.Session.SocketSession = null;
                    }
                    KillProcess(task.ProcessId);
                    taskManager.RemoveTask(task.Id);
                    SoftDelete($"{parameter.WorkDir}\\{task.Id}");
                    logger.Debug($"Cleanup complete");
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

        private bool GetBoolean(string value)
        {
            if (value.ToLowerInvariant() == "true")
                return true;
            else if (value.ToLowerInvariant() == "false")
                return false;
            else
                return Boolean.TryParse(value, out var boolResult);
        }

        private ContentData GetContentData(string fullname)
        {
            var contentData = new ContentData()
            {
                ContentType = $"application/{Path.GetExtension(fullname).Replace(".", "")}",
                ExternalPath = Path.GetFileName(fullname),
                FileData = File.ReadAllBytes(fullname),
            };

            return contentData;
        }

        private bool SoftDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                    logger.Debug($"The temp dir {folder} was deleted.");
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"The temp dir {folder} could not deleted.");
                return false;
            }
        }

        private int StartDeliveryTool(ActiveTask task, UserParameter parameter)
        {
            try
            {
                var jobResultPath = Path.Combine(parameter.WorkDir, task.Id, "JobResults");
                var distribute = new Ser.Distribute.Distribute();
                var privateKeyFullname = PathUtils.GetFullPathFromApp(parameter.PrivateKeyPath);
                var result = distribute.Run(jobResultPath, privateKeyFullname);
                task.Distribute = result;
                return 3;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The delivery tool could not start as process.");
                return -1;
            }
        }

        private int Status(string workDir, int sessionStatus)
        {
            var status = GetStatus(workDir);
            logger.Trace($"Report status {status}");
            switch (status)
            {
                case EngineResult.SUCCESS:
                    return 2;
                case EngineResult.ABORT:
                    return 1;
                case EngineResult.ERROR:
                    logger.Error("No Report created (ERROR).");
                    return -1;
                case EngineResult.WARNING:
                    logger.Warn("No Report created (WARNING).");
                    return -1;
                default:
                    return sessionStatus;
            }
        }

        private string GetResultFile(string workDir)
        {
            var resultFolder = Path.Combine(workDir, "JobResults");
            if (Directory.Exists(resultFolder))
            {
                var resultFiles = new DirectoryInfo(resultFolder).GetFiles("*.json", SearchOption.TopDirectoryOnly).ToList();
                var sortFiles = resultFiles.OrderBy(f => f.LastWriteTime).Reverse();
                return sortFiles.FirstOrDefault().FullName;
            }

            return null;
        }

        private JObject GetJsonObject(string workDir)
        {
            var resultFile = GetResultFile(workDir);
            if (File.Exists(resultFile))
            {
                logger.Trace($"json file {resultFile} found.");
                using (var fs = new FileStream(resultFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var sr = new StreamReader(fs, true))
                    {
                        var json = sr.ReadToEnd();
                        return JsonConvert.DeserializeObject<JObject>(json);
                    }
                }
            }

            logger.Trace($"json file {resultFile} not found.");
            return null;
        }

        private string GetReportFile(string workDir, string taskId)
        {
            try
            {
                var jobject = GetJsonObject(workDir);
                var path = jobject["reports"].FirstOrDefault()["paths"].FirstOrDefault().Value<string>() ?? null;
                return path;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private EngineResult GetStatus(string workDir)
        {
            try
            {
                var jobject = GetJsonObject(workDir);
                var result = jobject?.Property("status")?.Value?.Value<string>() ?? null;
                logger.Trace($"EngineResult: {result}");
                if (result != null)
                    return (EngineResult)Enum.Parse(typeof(EngineResult), result, true);
                else
                    return EngineResult.UNKOWN;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return EngineResult.UNKOWN;
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