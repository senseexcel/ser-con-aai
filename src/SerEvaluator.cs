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
    #endregion

    public class SerEvaluator : ConnectorBase, IDisposable
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
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
        #endregion

        #region Properties & Variables
        private static SerOnDemandConfig OnDemandConfig;
        private SessionManager sessionManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            OnDemandConfig = config;
            sessionManager = new SessionManager();            
            ServicePointManager.ServerCertificateValidationCallback += ValidateRemoteCertificate;
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
                    PluginVersion = OnDemandConfig.AppVersion,
                    PluginIdentifier = OnDemandConfig.AppName,
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

                logger.Debug($"Function id: {functionRequestHeader.FunctionId}");
                var result = new OnDemandResult() { Status = 0 };
                var row = GetParameter(requestStream);
                var json = GetParameterValue(0, row);

                var functionCall = (SerFunction)functionRequestHeader.FunctionId;
                SessionInfo session = null;
                string taskId = String.Empty;
                dynamic jsonObject;
                switch (functionCall)
                {
                    case SerFunction.START:
                        result = CreateReport(userParameter, json);
                        break;
                    case SerFunction.STATUS:
                        #region Status
                        var version = GitVersionInformation.InformationalVersion;
                        if (!String.IsNullOrEmpty(json))
                        {
                            jsonObject = JObject.Parse(json);
                            taskId = jsonObject?.TaskId ?? null;
                        }
                        session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, userParameter);
                        if (session == null)
                        {
                            logger.Debug($"No existing session with id {taskId} found.");
                            result = new OnDemandResult() { Status = 0, Version = version };
                        }
                        else
                        {
                            if (String.IsNullOrEmpty(taskId))
                                result = new OnDemandResult() { Status = 0, TaskId = session.TaskId, Version = version };
                            else
                            {
                                result = new OnDemandResult() { Status = session.Status, Link = session.DownloadLink };
                            }
                        }
                        break; 
                    #endregion
                    case SerFunction.STOP:
                        #region Stop
                        jsonObject = JObject.Parse(json);
                        taskId = jsonObject?.TaskId ?? null;
                        session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, userParameter);
                        if (session == null)
                            throw new Exception("No existing session found.");

                        var process = GetProcess(session.ProcessId);
                        if (process != null)
                        {
                            process?.Kill();
                            Thread.Sleep(1000);
                        }
                        var workDir = PathUtils.GetFullPathFromApp(OnDemandConfig.WorkingDir);
                        SoftDelete($"{workDir}\\{taskId}");
                        session.Status = 0;
                        session.DownloadLink = null;
                        result = new OnDemandResult() { Status = session.Status };
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

            if (!OnDemandConfig.Connection.SslVerify)
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
                var thumbprints = OnDemandConfig.Connection.SslValidThumbprints;
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

        private OnDemandResult CreateReport(UserParameter parameter, string json)
        {
            try
            {
                var taskId = Guid.NewGuid().ToString();
                logger.Debug($"New Task-ID: {taskId}");
                var workDir = PathUtils.GetFullPathFromApp(OnDemandConfig.WorkingDir);
                var currentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {currentWorkingDir}");
                Directory.CreateDirectory(currentWorkingDir);

                //Caution: Personal//Me => Desktop Mode
                if (parameter.DomainUser.UserId == "sa_scheduler" &&
                    parameter.DomainUser.UserDirectory == "INTERNAL")
                {
                    //In Doku mit aufnehmen / Security rule für Task User ser_scheduler
                    parameter.DomainUser = new DomainUser("INTERNAL\\ser_scheduler");
                    var tmpsession = sessionManager.GetSession(OnDemandConfig.Connection, parameter);
                    var qrshub = new QlikQrsHub(OnDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                    var result = qrshub.SendRequestAsync($"app/{parameter.AppId}", HttpMethod.Get).Result;
                    var hubInfo = JsonConvert.DeserializeObject<HubInfo>(result);
                    if (hubInfo == null)
                        throw new Exception($"No app owner for app id {parameter.AppId} found.");
                    parameter.DomainUser = new DomainUser($"{hubInfo.Owner.UserDirectory}\\{hubInfo.Owner.UserId}");
                }

                //Get a session
                var session = sessionManager.GetSession(OnDemandConfig.Connection, parameter);
                if (session == null)
                    logger.Error("No session generated.");

                //check session is running
                if (session.Status == 1 || session.Status == 2)
                {
                    logger.Debug("Session ist already running.");
                    return new OnDemandResult() { TaskId = session.TaskId };
                }

                session.User = parameter.DomainUser;
                parameter.ConnectCookie = session?.Cookie;
                session.DownloadLink = null;
                session.Status = 1;
                session.TaskId = taskId;

                //get engine config
                var newEngineConfig = GetNewJson(parameter, json, currentWorkingDir, session.Cookie);

                //ondemand for download link ???
                var firstTask = newEngineConfig?.Tasks?.FirstOrDefault() ?? null;
                if (firstTask != null)
                    parameter.OnDemand = firstTask.Template.Output == "OnDemand";

                //save template from content libary
                FindTemplatePaths(parameter, newEngineConfig, currentWorkingDir, session.Cookie);
               
                //Save config for SER engine
                Directory.CreateDirectory(currentWorkingDir);
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                var serConfig = JsonConvert.SerializeObject(newEngineConfig, Formatting.Indented);
                File.WriteAllText(savePath, serConfig);

                //Start SER Engine as Process
                logger.Debug($"Start Engine \"{currentWorkingDir}\"");
                var serProcess = new Process();
                serProcess.StartInfo.FileName = PathUtils.GetFullPathFromApp(OnDemandConfig.SerEnginePath);
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();
                session.ProcessId = serProcess.Id;

                var statusThread = new Thread(() => CheckStatus(currentWorkingDir, parameter))
                {
                    IsBackground = true
                };
                statusThread.Start();

                return new OnDemandResult() { TaskId = taskId };
            }
            catch (Exception ex)
            {
                throw new Exception("The report could not be created.", ex);
            }
        }

        private Process GetProcess(int id)
        {
            try
            {
                return Process.GetProcessById(id);
            }
            catch
            {
                return null;
            }
        }

        private void FindTemplatePaths(UserParameter parameter, SerConfig config, string workDir, Cookie cookie)
        {
            foreach (var task in config.Tasks)
            {
                var templateUri = new Uri(task.Template.Input);
                if (templateUri.Scheme.ToLowerInvariant() == "content")
                {
                    var contentFiles = GetLibraryContent(OnDemandConfig.Connection.ServerUri, parameter.AppId, cookie, templateUri.Host);
                    logger.Debug($"File count in content library: {contentFiles?.Count}");
                    var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(templateUri.AbsolutePath));
                    if (filterFile != null)
                    {
                        var savePath = DownloadFile(filterFile, workDir, cookie);
                        task.Template.Input = Path.GetFileName(savePath);
                    }
                    else
                        logger.Warn($"No file in content library found.");
                }
                else if (templateUri.Scheme.ToLowerInvariant() == "lib")
                {
                    var connections = GetConnections(OnDemandConfig.Connection.ServerUri, parameter.AppId, cookie);
                    dynamic libResult = connections?.FirstOrDefault(n => n["qName"]?.ToString()?.ToLowerInvariant() == templateUri.Host) ?? null;
                    if (libResult != null)
                    {
                        var libPath = libResult.qConnectionString.ToString();
                        var relPath = templateUri.LocalPath.TrimStart(new char[] { '\\', '/' }).Replace("/", "\\");
                        task.Template.Input = $"{libPath}{relPath}";
                    }
                    else
                        logger.Warn($"No path in connection library found.");
                }
                else
                {
                    throw new Exception($"Unknown Scheme in Filename Uri {task.Template.Input}.");
                }
            }
        }

        private string DownloadFile(string relUrl, string workdir, Cookie cookie)
        {
            var savePath = Path.Combine(workdir, Path.GetFileName(relUrl));
            var webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
            webClient.DownloadFile($"{OnDemandConfig.Connection.ServerUri.AbsoluteUri}{relUrl}", savePath);
            return savePath;
        }

        private SerConfig GetNewJson(UserParameter parameter, string userJson, string workdir, Cookie cookie)
        {
            logger.Debug("serialize config connection.");
            var mainConnection = OnDemandConfig.Connection;
            if (mainConnection.Credentials != null)
            {
                var cred = mainConnection.Credentials;
                cred.Value = cookie.Value;
            }

            var configCon = JObject.Parse(JsonConvert.SerializeObject(mainConnection, Formatting.Indented));
            logger.Debug("parse user json.");
            var jsonConfig = HjsonValue.Parse(userJson).ToString();
            var serConfig = JObject.Parse(jsonConfig);
            logger.Debug("search for connections.");
            var tasks = serConfig["tasks"].ToList() ?? new List<JToken>();
            foreach (var task in tasks)
            {
                //merge connections / config <> script
                var currentConnection = task["connection"];
                configCon.Merge(currentConnection);
                configCon["credentials"]["privateKey"] = null;
                configCon["credentials"]["cert"] = null;
                task["connection"] = configCon;
                var distribute = task["distribute"];
                var children = distribute.Children().Children();
                foreach (var child in children)
                {
                    var connection = child["connection"] ?? null;
                    if (connection?.ToString() == "@CONFIGCONNECTION@")
                        child["connection"] = configCon;
                }
            }

            return JsonConvert.DeserializeObject<SerConfig>(serConfig.ToString());
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

        private void CheckStatus(string currentWorkingDir, UserParameter parameter)
        {
            var status = 0;
            var session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, parameter);
            while (status != 2)
            {
                Thread.Sleep(250);
                status = Status(currentWorkingDir);
                if (status == -1 || status == 0 || session.Status == 0)
                    break;
            }

            //Engine finish
            session.Status = status;
            if (session.Status != 2)
                return;

            //Delivery finish
            status = StartDeliveryTool(currentWorkingDir, session, parameter.OnDemand);

            //sessionManager.DeleteSession(new Uri(OnDemandConfig.Connection.ConnectUri), parameter.DomainUser, taskId);
            SoftDelete(currentWorkingDir);
            session.Status = status;
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
                Directory.Delete(folder, true);
                logger.Debug($"work dir {folder} deleted.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"The Folder {folder} could not deleted.");
                return false;
            }
        }

        private int StartDeliveryTool(string workdir, SessionInfo session, bool ondemand = false)
        {
            try
            {
                var jobResultPath = Path.Combine(workdir, "JobResults");
                var distribute = new Distribute();
                var result = distribute.Run(jobResultPath, ondemand);
                if (result != null)
                {
                    session.DownloadLink = result;
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

        private int Status(string workDir)
        {
            var status = GetStatus(workDir);
            logger.Debug($"Report status {status}");
            switch (status)
            {
                case "SUCCESS":
                    return 2;
                case "ABORT":
                    return 1;
                case "ERROR":
                    logger.Error("No Report created.");
                    return -1;
                default:
                    return 1;
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
                logger.Debug($"json file {resultFile} found.");
                var json = File.ReadAllText(resultFile);
                return JsonConvert.DeserializeObject<JObject>(json);
            }

            logger.Error($"json file {resultFile} not found.");
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

        private string GetStatus(string workDir)
        {
            try
            {
                var jobject = GetJsonObject(workDir);
                return jobject?.Property("status")?.Value?.Value<string>() ?? null;
            }
            catch(Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }
        #endregion
    }
}