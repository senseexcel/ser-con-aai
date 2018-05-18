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
    using Grpc.Core;
    using Qlik.Sse;
    using Google.Protobuf;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using static Qlik.Sse.Connector;
    using NLog;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Q2g.HelperQrs;
    using Ser.Api;
    using Hjson;
    using System.Net.Http;
    using Newtonsoft.Json.Serialization;
    using Ser.Distribute;
    using System.Security.Cryptography.X509Certificates;
    using System.Net.Security;
    using System.Reflection;
    using GitInformation;
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
            UNKOWN
        }
        #endregion

        #region Properties & Variables
        private static SerOnDemandConfig onDemandConfig;
        private TaskManager taskManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            onDemandConfig = config;
            taskManager = new TaskManager();            
            ServicePointManager.ServerCertificateValidationCallback += ValidateRemoteCertificate;
            //RestoreTasks();
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

        public override async Task ExecuteFunction(IAsyncStreamReader<BundledRows> requestStream, IServerStreamWriter<BundledRows> responseStream, ServerCallContext context)
        {
            try
            {
                logger.Debug("ExecuteFunction was called");
                Thread.Sleep(200);
                var functionRequestHeaderStream = context.RequestHeaders.SingleOrDefault(header => header.Key == "qlik-functionrequestheader-bin");
                if (functionRequestHeaderStream == null)
                {
                    throw new Exception("ExecuteFunction called without Function Request Header in Request Headers.");
                }

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
                };

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                var result = new OnDemandResult() { Status = 0 };
                var row = GetParameter(requestStream);
                var json = GetParameterValue(0, row);

                var functionCall = (SerFunction)functionRequestHeader.FunctionId;
                logger.Debug($"Function id: {functionCall}");
                var workDir = PathUtils.GetFullPathFromApp(onDemandConfig.WorkingDir);
                dynamic jsonObject;

                //Caution: Personal//Me => Desktop Mode
                if (userParameter.DomainUser.UserId == "sa_scheduler" &&
                    userParameter.DomainUser.UserDirectory == "INTERNAL")
                {
                    //In Doku mit aufnehmen / Security rule für Task User ser_scheduler
                    userParameter.DomainUser = new DomainUser("INTERNAL\\ser_scheduler");
                    logger.Debug($"New DomainUser: {userParameter.DomainUser.ToString()}");
                    var tmpsession = taskManager.GetSession(onDemandConfig.Connection, userParameter);
                    var qrshub = new QlikQrsHub(onDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                    var qrsResult = qrshub.SendRequestAsync($"app/{userParameter.AppId}", HttpMethod.Get).Result;
                    var hubInfo = JsonConvert.DeserializeObject<HubInfo>(qrsResult);
                    if (hubInfo == null)
                        throw new Exception($"No app owner for app id {userParameter.AppId} found.");
                    userParameter.DomainUser = new DomainUser($"{hubInfo.Owner.UserDirectory}\\{hubInfo.Owner.UserId}");
                }

                string taskId = null;
                string versions = null;
                string tasks = null;
                ActiveTask activeTask = null;

                switch (functionCall)
                {
                    case SerFunction.START:
                        result = CreateReport(userParameter, json, workDir);
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
                                //if (tasks == "all")
                                //    statusResult.Tasks = taskManager.GetAllTasksForUser(session.ConnectUri, session.Cookie, userParameter.DomainUser);

                                statusResult.Status = activeTask.Status;
                                statusResult.Link = activeTask.DownloadLink;
                            }
                            else
                            {
                                logger.Debug($"No existing task id {taskId} found.");
                            }
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
                            //var results = taskManager.GetAllTaskForAppId(userParameter.AppId);
                            //foreach (var task in results)
                            //    if(KillProcess(activeTask.ProcessId))
                            //        taskManager.RemoveTask(task.t);
                        }
                        else
                        {
                            logger.Debug($"Reset Status");
                            KillProcess(activeTask.ProcessId);
                            SoftDelete($"{workDir}\\{taskId}");
                            taskManager.RemoveTask(activeTask.TaskId);
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
        private static bool ValidateRemoteCertificate(object sender, X509Certificate cert, X509Chain chain,
                                                      SslPolicyErrors error)
        {
            if (error == SslPolicyErrors.None)
                return true;

            if (!onDemandConfig.Connection.SslVerify)
                return true;

            Uri requestUri = null;
            if (sender is HttpRequestMessage hrm)
                requestUri = hrm.RequestUri;
            if (sender is HttpClient hc)
                requestUri = hc.BaseAddress;
            if (sender is HttpWebRequest hwr)
                requestUri = hwr.Address;

            if (requestUri != null)
            {
                logger.Debug("Validate Thumbprints...");
                var thumbprints = onDemandConfig.Connection.SslValidThumbprints;
                foreach (var item in thumbprints)
                {
                    try
                    {
                        Uri uri = null;
                        if (!String.IsNullOrEmpty(item.Url))
                            uri = new Uri(item.Url);
                        var thumbprint = item.Thumbprint.Replace(":", "").Replace(" ", "");
                        if (thumbprint == cert.GetCertHashString() && uri == null || 
                            thumbprint == cert.GetCertHashString() &&
                            uri.Host.ToLowerInvariant() == requestUri.Host.ToLowerInvariant())
                            return true;
                    }
                    catch { }
                }
            }

            return false;
        }

        private OnDemandResult CreateReport(UserParameter parameter, string json, string workDir)
        {
            var taskId = String.Empty;

            try
            {
                var id = new DirectoryInfo(workDir).Name;
                var currentWorkingDir = String.Empty;
                if (!Guid.TryParse(id, out var result))
                {
                    taskId = Guid.NewGuid().ToString();
                    currentWorkingDir = Path.Combine(workDir, taskId);
                    Directory.CreateDirectory(currentWorkingDir);
                }
                else
                {
                    taskId = id;
                    currentWorkingDir = workDir;
                }

                logger.Debug($"New Task-ID: {taskId}");
                logger.Debug($"TempFolder: {currentWorkingDir}");

                //check for running task
                var task = taskManager.GetRunningTask(taskId);
                if(task != null)
                {
                    if (task.Status == 1 || task.Status == 2)
                    {
                        logger.Debug("Session ist already running.");
                        return new OnDemandResult() { TaskId = task.TaskId };
                    }
                }

                var newTask = new ActiveTask()
                {
                    Status = 1,
                    TaskId = taskId,
                    StartTime = DateTime.Now,
                    AppId = parameter.AppId,
                };

                //get a session
                var session = taskManager.GetSession(onDemandConfig.Connection, parameter, newTask);
                if (session == null)
                {
                    SoftDelete(currentWorkingDir);
                    throw new Exception("No session cookie generated.");
                }
                parameter.ConnectCookie = session?.Cookie;
         
                //get engine config
                var newEngineConfig = GetNewJson(parameter, json, currentWorkingDir);

                //save template from content libary
                FindTemplatePaths(parameter, newEngineConfig, currentWorkingDir);

                //Save config for SER engine
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                var serConfig = JsonConvert.SerializeObject(newEngineConfig, Formatting.Indented);
                File.WriteAllText(savePath, serConfig);

                //Start SER Engine as Process
                logger.Debug($"Start Engine \"{currentWorkingDir}\"...");
                var serProcess = new Process();
                serProcess.StartInfo.FileName = PathUtils.GetFullPathFromApp(onDemandConfig.SerEnginePath);
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();
                newTask.ProcessId = serProcess.Id;

                //Write process file
                var procFileName = $"{Path.GetFileNameWithoutExtension(serProcess.StartInfo.FileName)}.pid";
                File.WriteAllText(Path.Combine(currentWorkingDir, procFileName),
                                  $"{serProcess.Id.ToString()}|{parameter.AppId.ToString()}|{parameter.DomainUser.ToString()}");

                var statusThread = new Thread(() => CheckStatus(currentWorkingDir, parameter, newTask))
                {
                    IsBackground = true
                };
                statusThread.Start();
                return new OnDemandResult() { TaskId = taskId, Status = 1 };
            }
            catch (Exception ex)
            {
                var task = taskManager.GetRunningTask(taskId);
                task.Status = -1;
                return new OnDemandResult() { TaskId = taskId, Status = -1 };
                throw new Exception("The report could not be created.", ex);
            }
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
                            };

                            var jobJsonFile = files.Where(f => f.EndsWith("userJson.hjson")).FirstOrDefault();
                            var json = File.ReadAllText(jobJsonFile);

                            //Remove Result file for distribute
                            var resultFile = files.Where(f => f.Contains("\\result_") && f.EndsWith(".json")).FirstOrDefault() ?? null;
                            if (resultFile != null)
                                SoftDelete(Path.GetDirectoryName(resultFile));

                            CreateReport(parameter, json, folder);
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
                var templateUri = new Uri(report.Template.Input);
                if (templateUri.Scheme.ToLowerInvariant() == "content")
                {
                    var contentFiles = GetLibraryContent(onDemandConfig.Connection.ServerUri, parameter.AppId, cookie, templateUri.Host);
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
                    var connections = GetConnections(onDemandConfig.Connection.ServerUri, parameter.AppId, cookie);
                    dynamic libResult = connections?.FirstOrDefault(n => n["qName"]?.ToString()?.ToLowerInvariant() == templateUri.Host) ?? null;
                    if (libResult != null)
                    {
                        var libPath = libResult.qConnectionString.ToString();
                        var relPath = templateUri.LocalPath.TrimStart(new char[] { '\\', '/' }).Replace("/", "\\");
                        report.Template.Input = $"{libPath}{relPath}";
                    }
                    else
                        throw new Exception($"No path in connection library found.");
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
            var bearerSerConnection = new SerConnection()
            {
                App = parameter.AppId,
                ServerUri = onDemandConfig.Connection.ServerUri,
                Credentials = new SerCredentials()
                {
                    Type = QlikCredentialType.HEADER,
                    Key = "Authorization",
                    Value = $"Bearer { token }"
                }
            };

            var bearerConnection = JToken.Parse(JsonConvert.SerializeObject(bearerSerConnection, Formatting.Indented));

            logger.Debug("serialize config connection.");
            if (mainConnection.Credentials != null)
            {
                var cred = mainConnection.Credentials;
                cred.Value = parameter.ConnectCookie.Value;
                parameter.PrivateKeyPath = cred.PrivateKey;
            }

            var configConnection = JObject.Parse(JsonConvert.SerializeObject(mainConnection, Formatting.Indented));
            configConnection["credentials"]["cert"] = null;
            configConnection["credentials"]["privateKey"] = null;

            logger.Debug("parse user json.");

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

            var jsonConfig = GetNormalizeJson(userJson);
            var serConfig = JObject.Parse(jsonConfig);

            //check for ondemand mode
            var ondemandObject = serConfig["onDemand"] ?? null;
            if (ondemandObject != null)
                parameter.OnDemand = serConfig["onDemand"]?.ToObject<bool>() ?? false;

            logger.Debug("search for connections.");
            var tasks = serConfig["tasks"]?.ToList() ?? null;
            foreach (var task in tasks)
            {
                var reports = task["reports"]?.ToList() ?? null;
                foreach (var report in reports)
                {
                    var userConnections = report["connections"].ToList();
                    var newUserConnections = new List<JToken>();
                    foreach (var userConnection in userConnections)
                    {
                        var cvalue = userConnection.ToString();
                        if (!cvalue.StartsWith("{"))
                            cvalue = $"{{{cvalue}}}";
                        var currentConnection = JObject.Parse(cvalue);
                        //merge connections / config <> script
                        currentConnection.Merge(configConnection);
                        newUserConnections.Add(currentConnection);
                        newUserConnections.Add(bearerConnection);
                    }

                    report["connections"] = new JArray(newUserConnections);
                    var distribute = report["distribute"];
                    var children = distribute.Children().Children();
                    foreach (var child in children)
                    {
                        var connection = child["connections"] ?? null;
                        if (connection?.ToString() == "@CONFIGCONNECTION@")
                            child["connections"] = configConnection;

                        var childProp = child.Parent as JProperty;
                        if (childProp?.Name == "hub")
                        {
                            var hubOwner = child["owner"] ?? null;
                            if (hubOwner == null)
                                child["owner"] = parameter.DomainUser.ToString();
                        }
                    }
                }
            }

            var result = JsonConvert.DeserializeObject<SerConfig>(serConfig.ToString());
            return result;
        }

        private List<JToken> GetConnections(Uri host, string appId, Cookie cookie)
        {
            var qlikWebSocket = new QlikWebSocket(host, cookie);
            var isOpen = qlikWebSocket.OpenSocket();
            dynamic response = qlikWebSocket.OpenDoc(appId);
            if (response.ToString().Contains("App already open"))
                response = qlikWebSocket.GetActiveDoc();
            var handle = response?.result?.qReturn?.qHandle?.ToString() ?? null;
            response = qlikWebSocket.GetConnections(handle);
            JArray results = response.result.qConnections;
            return results.ToList();
        }

        private List<string> GetLibraryContentInternal(QlikWebSocket qlikWebSocket, string handle, string qName)
        {
            dynamic response = qlikWebSocket.GetLibraryContent(handle, qName);
            JArray qItems = response?.result?.qList?.qItems;
            var qUrls = qItems.Select(j => j["qUrl"].ToString()).ToList();
            return qUrls;
        }

        private List<string> GetLibraryContent(Uri serverUri, string appId, Cookie cookie, string contentName = "")
        {            
            try
            {
                var results = new List<string>();
                var qlikWebSocket = new QlikWebSocket(serverUri, cookie);
                var isOpen = qlikWebSocket.OpenSocket();
                dynamic response = qlikWebSocket.OpenDoc(appId);
                if (response.ToString().Contains("App already open"))
                    response = qlikWebSocket.GetActiveDoc();
                var handle = response?.result?.qReturn?.qHandle?.ToString() ?? null;

                var readItems = new List<string>() { contentName };
                if (String.IsNullOrEmpty(contentName))
                {
                    // search for all App Specific ContentLibraries                    
                    response = qlikWebSocket.GetContentLibraries(handle);
                    JArray qItems = response?.result?.qList?.qItems.ToObject<JArray>();
                    readItems = qItems.Where(s => s["qAppSpecific"]?.Value<bool>() == true).Select(s => s["qName"].ToString()).ToList();
                }
                    
                foreach (var item in readItems)
                {                    
                    var qUrls = GetLibraryContentInternal(qlikWebSocket, handle, item);
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

        private void CheckStatus(string currentWorkingDir, UserParameter parameter, ActiveTask task)
        {
            var status = 0;
            while (status != 2)
            {
                Thread.Sleep(250);
                status = Status(currentWorkingDir, task.Status);
                if (status == -1 || status == 0)
                    break;
            }

            //Reports Generated
            task.Status = status;
            if (task.Status != 2)
                return;

            //Delivery finish
            status = StartDeliveryTool(currentWorkingDir, task, parameter.OnDemand, parameter.PrivateKeyPath);
            task.Status = status;
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
                    logger.Debug($"work dir {folder} deleted.");
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"The Folder {folder} could not deleted.");
                return false;
            }
        }

        private int StartDeliveryTool(string workdir, ActiveTask task, bool ondemand = false, string privateKeyPath = null)
        {
            try
            {
                var jobResultPath = Path.Combine(workdir, "JobResults");
                var distribute = new Distribute();
                var privateKeyFullname = PathUtils.GetFullPathFromApp(privateKeyPath);
                var result = distribute.Run(jobResultPath, ondemand, privateKeyFullname);
                if (result != null)
                {
                    task.DownloadLink = result;
                    return 3;
                }
                else
                    return -1;
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
                    logger.Error("No Report created.");
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
                logger.Error(ex);
                return null;
            }
        }
        #endregion
    }
}