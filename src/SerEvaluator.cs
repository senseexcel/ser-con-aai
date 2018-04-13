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
            ABORT = 3,
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
                        //new FunctionDefinition()
                        //{
                        //     FunctionId = 1,
                        //     FunctionType = FunctionType.Scalar,
                        //     Name = SerFunction.CREATE.ToString(),
                        //     Params =
                        //     {
                        //        new Parameter() { Name = "Request", DataType = DataType.String },
                        //     },
                        //     ReturnType = DataType.String,
                        //},
                        new FunctionDefinition()
                        {
                            FunctionId = 1,
                            FunctionType = FunctionType.Scalar,
                            Name = SerFunction.START.ToString(),
                            Params =
                            {
                                new Parameter() { Name = "Script", DataType = DataType.String }
                            },
                            ReturnType = DataType.String
                        },
                        new FunctionDefinition()
                        {
                             FunctionId = 2,
                             FunctionType = FunctionType.Scalar,
                             Name = SerFunction.STATUS.ToString(),
                             Params =
                             {
                                new Parameter() { Name = "Request", DataType = DataType.String },
                             },
                             ReturnType = DataType.String
                        },
                        new FunctionDefinition()
                        {
                            FunctionId = 3,
                            FunctionType = FunctionType.Scalar,
                            Name = SerFunction.ABORT.ToString(),
                            Params =
                            {
                               new Parameter() { Name = "Request", DataType = DataType.String },
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
                var result = new OnDemandResult() { Status = -1 };
                var row = GetParameter(requestStream);
                var json = GetParameterValue(0, row);

                //if (functionRequestHeader.FunctionId == (int)SerFunction.CREATE)
                //{
                //    userParameter.OnDemand = true;
                //    var extParam = JObject.Parse(json);
                //    userParameter.TemplateFileName = extParam["template"].ToString();
                //    logger.Debug($"Template path: {userParameter.TemplateFileName}");
                //    userParameter.SaveFormats = extParam["output"].ToString();
                //    logger.Debug($"SaveFormat: {userParameter.SaveFormats}");
                //    var mode = GetBoolean(extParam["selectionMode"].ToString());
                //    if (mode)
                //        userParameter.UseUserSelesction = SelectionMode.OnDemandOn;
                //    else
                //        userParameter.UseUserSelesction = SelectionMode.OnDemandOff;
                //    logger.Debug($"UseSelection: {userParameter.UseUserSelesction}");

                //    result = CreateReport(userParameter, true);
                //}
                if (functionRequestHeader.FunctionId == (int)SerFunction.START)
                {
                    result = CreateReport(userParameter, json);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.STATUS)
                {
                    //Status -1=Fail 1=Running, 2=Success, 3=DeleverySuccess, 4=StopSuccess, 5=Download
                    var taskId = JObject.Parse(json)["TaskName"].ToString();
                    var session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, domainUser);
                    if (session == null)
                    {
                        logger.Error($"No existing session with id {taskId} found.");
                        result = new OnDemandResult() { Status = -1 };
                    }

                    if (session.DownloadLink != null)
                    {
                        session.Status = 5;
                        result = new OnDemandResult() { Status = 5, Link = session.DownloadLink };
                    }
                    else
                        result = new OnDemandResult() { Status = session.Status };
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.ABORT)
                {
                    var taskId = JObject.Parse(json)["TaskId"].ToString();
                    var session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, domainUser);
                    if (session == null)
                        throw new Exception("No existing session found.");
                    var process = Process.GetProcessById(session.ProcessId);
                    if (!process.HasExited)
                    {
                        process?.Kill();
                        Thread.Sleep(1000);
                    }
                    SoftDelete($"{OnDemandConfig.WorkingDir}\\{taskId}");
                    session.Status = 4;
                    result = new OnDemandResult() { Status = 4 };
                }
                else
                {
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
                        var uri = new Uri(item.Url);
                        var thumbprint = item.Thumbprint.Replace(":", "").Replace(" ", "");
                        if (thumbprint == cert.GetCertHashString() &&
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
                var workDir = OnDemandConfig.WorkingDir;
                var currentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {currentWorkingDir}");
                Directory.CreateDirectory(currentWorkingDir);

                //Caution: Personal//Me => Desktop Mode
                if (parameter.DomainUser.UserId == "sa_scheduler" &&
                    parameter.DomainUser.UserDirectory == "INTERNAL")
                {
                    //In Doku mit aufnehmen / Security rule für Task User ser_scheduler
                    var tmpsession = sessionManager.GetSession(OnDemandConfig.Connection, new DomainUser("INTERNAL\\ser_scheduler"));
                    var qrshub = new QlikQrsHub(OnDemandConfig.Connection.ServerUri, tmpsession.Cookie);
                    var result = qrshub.SendRequestAsync($"app/{parameter.AppId}", HttpMethod.Get).Result;
                    var hubInfo = JsonConvert.DeserializeObject<HubInfo>(result);
                    if (hubInfo == null)
                        throw new Exception($"No app owner for app id {parameter.AppId} found.");
                    parameter.DomainUser = new DomainUser($"{hubInfo.Owner.UserDirectory}\\{hubInfo.Owner.UserId}");
                }

                //Get a session
                var session = sessionManager.GetSession(OnDemandConfig.Connection, parameter.DomainUser);
                if (session == null)
                    logger.Error("No session generated.");
                session.User = parameter.DomainUser;
                parameter.ConnectCookie = session?.Cookie;
                session.DownloadLink = null;

                //get engine config
                var newEngineConfig = GetNewJson(parameter, json, currentWorkingDir, session.Cookie);

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
                serProcess.StartInfo.FileName = OnDemandConfig.SerEnginePath;
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();
                session.ProcessId = serProcess.Id;

                var statusThread = new Thread(() => CheckStatus(taskId, currentWorkingDir, parameter))
                {
                    IsBackground = true
                };
                statusThread.Start();

                return new OnDemandResult() { TaskName = taskId };
            }
            catch (Exception ex)
            {
                throw new Exception("The report could not be created.", ex);
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
                    var libResult = connections?.FirstOrDefault(n => n["qName"]?.ToString()?.ToLowerInvariant() == templateUri.Host) ?? null;
                    if (libResult != null)
                    {
                        var libPath = libResult["qConnectionString"].ToString();
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
                if (cred.PrivateKey != null)
                    cred.PrivateKey = null;
                if (cred.Cert != null)
                    cred.Cert = null;
            }
            var configCon = JObject.Parse(JsonConvert.SerializeObject(mainConnection));
            logger.Debug("parse user json.");
            var jsonConfig = HjsonValue.Parse(userJson).ToString();
            var serConfig = JObject.Parse(jsonConfig);
            logger.Debug("search for connections.");
            var tasks = serConfig["tasks"]?.ToList() ?? new List<JToken>();
            foreach (var task in tasks)
            {
                //merge connections / config <> script
                var currentConnection = task["connection"].ToObject<JObject>();
                configCon.Merge(currentConnection);
                task["connection"] = configCon;

                var children = task["distribute"].Children().Children();
                foreach (var child in children)
                {
                    var connection = child["connection"] ?? null;
                    if (connection?.ToString() == "@CONFIGCONNECTION@")
                    {
                        child["connection"] = configCon;
                    }
                }
            }

            //Code zum Debuggen des Converters
            var test = new SingleValueArrayConverter();
            test.CanConvert(typeof(string));

            //Values von Selections werden aufgelöst, aber value nicht, dass ist null!!!
            return JsonConvert.DeserializeObject<SerConfig>(serConfig.ToString());
        }

        //private string GetNewJson2(UserParameter parameter, string json, string workdir, Cookie cookie)
        //{
        //    try
        //    {
        //        logger.Debug($"Websocket host: {OnDemandConfig.Connection.ServerUri.ToString()}");
        //        var serConfig = JObject.Parse(HjsonValue.Parse(json).ToString());
        //        var tasks = serConfig["tasks"].ToList();
        //        foreach (var task in tasks)
        //        {
        //            var scriptCon = JObject.Parse(task["connection"].ToString());
        //            OnDemandConfig.Connection.Credentials.Value = cookie.Value;
        //            var configCon = JObject.Parse(JsonConvert.SerializeObject(OnDemandConfig.Connection));
        //            configCon.Merge(scriptCon);
        //            var currentConnection = configCon.Value<JObject>();
        //            currentConnection["credentials"] = currentConnection["credentials"].RemoveFields(new string[] { "cert", "privateKey" });
        //            task["connection"] = currentConnection;

        //            var children = task["distribute"].Children().Children();
        //            foreach (var child in children)
        //            {
        //                var connection = child["connection"] ?? null;
        //                if (connection?.ToString() == "@CONFIGCONNECTION@")
        //                {
        //                    child["connection"] = currentConnection;
        //                }
        //            }

        //            var appId = task["connection"]["app"].ToString();
        //            var fileName = task["template"]["input"].ToString();
        //            if (fileName.ToLowerInvariant().StartsWith("content://"))
        //            {
        //                var contentFiles = new List<string>();
        //                var contentUri = new Uri(fileName);
        //                // ToDo: FIX issue
        //                contentFiles = GetLibraryContent(OnDemandConfig.Connection.ServerUri, appId, cookie, contentUri.Host);

        //                logger.Debug($"File count in content library: {contentFiles?.Count}");
        //                var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(contentUri.AbsolutePath));
        //                if (filterFile != null)
        //                {
        //                    var savePath = DownloadFile(filterFile, workdir, cookie);
        //                    task["template"]["input"] = Path.GetFileName(savePath);
        //                    logger.Debug($"Filename {fileName} in content library found.");
        //                }
        //                else
        //                    logger.Warn($"No file in content library found.");
        //            }
        //            else if (fileName.ToLowerInvariant().StartsWith("lib://"))
        //            {
        //                var libUri = new Uri(fileName);
        //                if (String.IsNullOrEmpty(libUri.Host))
        //                    throw new Exception("Unknown Name of the lib connection.");
                        
        //                var connections = GetConnections(OnDemandConfig.Connection.ServerUri, appId, cookie);
        //                var libResult = connections.FirstOrDefault(n => n["qName"].ToString().ToLowerInvariant() == libUri.Host);
        //                var libPath = libResult["qConnectionString"].ToString();
        //                var relPath = libUri.LocalPath.TrimStart(new char[] { '\\', '/' }).Replace("/", "\\");
        //                task["template"]["input"] = $"{libPath}{relPath}";
        //            }
        //            else
        //            {
        //                throw new Exception($"Unknown Sheme in Filename Uri {fileName}.");
        //            }
        //        }
        //        return serConfig.ToString();
        //    }
        //    catch (Exception ex)
        //    {
        //        logger.Error(ex, $"The propertys for ser config could not be set. Json: {json}");
        //        return null;
        //    }
        //}

        private List<JToken> GetConnections(Uri host, string appId, Cookie cookie)
        {
            var results = new List<string>();
            var qlikWebSocket = new QlikWebSocket(host, cookie);
            var isOpen = qlikWebSocket.OpenSocket();
            var response = qlikWebSocket.OpenDoc(appId);
            var handle = response["result"]["qReturn"]["qHandle"].ToString();
            response = qlikWebSocket.GetConnections(handle);
            return response["result"]["qConnections"].ToList();
        }

        private List<string> GetLibraryContentInternal(QlikWebSocket qlikWebSocket, string handle, string qName)
        {
            var response = qlikWebSocket.GetLibraryContent(handle, qName);
            var qItems = response["result"]["qList"]["qItems"].ToList();
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
                var response = qlikWebSocket.OpenDoc(appId);
                var handle = response["result"]["qReturn"]["qHandle"].ToString();

                var readItems = new List<string>() { contentName };
                if (String.IsNullOrEmpty(contentName))
                {
                    // search for all App Specific ContentLibraries                    
                    response = qlikWebSocket.GetContentLibraries(handle);
                    var qItems = response["result"]["qList"]["qItems"].ToList();
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

        private string GetNewSerConfig(string templatePath, UserParameter parameter)
        {
            try
            {
                var task = new SerTask()
                {
                    General = new SerGeneral()
                    {
                        UseUserSelections = parameter.UseUserSelesction,
                    },
                    Template = new SerTemplate()
                    {
                        Input = Path.GetFileName(templatePath),
                        Output = $"OnDemandReport.{parameter.SaveFormats}",
                    },
                    Connection = new SerConnection()
                    {
                        App = parameter.AppId,
                        ServerUri = OnDemandConfig.Connection.ServerUri,
                        SslValidThumbprints = OnDemandConfig.Connection.SslValidThumbprints,
                        SslVerify = OnDemandConfig.Connection.SslVerify,
                        Credentials = new SerCredentials()
                        {
                            Type = QlikCredentialType.SESSION,
                            Key = parameter.ConnectCookie.Name,
                            Value = parameter.ConnectCookie.Value,
                        }
                    }
                };

                var hubSettings = new HubSettings()
                {
                    Mode = DistributeMode.OVERRIDE,
                    Type = SettingsType.HUB,
                    Owner = parameter.DomainUser.ToString(),
                    Connection = task.Connection,
                };

                var hubContent = JObject.Parse(JsonConvert.SerializeObject(hubSettings).ToString());
                task.Distribute = new JObject(new JProperty("hub", hubContent)); 
                var appConfig = new SerConfig() { Tasks = new List<SerTask> { task } }; 
                var jConfig = JsonConvert.SerializeObject(appConfig);
                var resultConfig = JObject.Parse(jConfig);
                resultConfig.RemoveFields(new string[] { "taskCount" });
                return resultConfig.ToString();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Config for SER-Engine not saved.");
                return null;
            }
        }

        private void CheckStatus(string taskId, string currentWorkingDir, UserParameter parameter)
        {
            var status = 0;
            var session = sessionManager.GetExistsSession(OnDemandConfig.Connection.ServerUri, parameter.DomainUser);
            session.Status = 1;
            while (status != 2)
            {
                Thread.Sleep(250);
                var result = Status(taskId);
                status = result.Status;
                if (status == -1)
                    break;
            }

            //Engine finish
            session.Status = status;
            if (status != 2)
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

        private OnDemandResult Status(string taskId)
        {
            var status = GetStatus(taskId);
            logger.Debug($"Report status {status}");
            if (status == "SUCCESS")
                return new OnDemandResult() { Status = 2 };
            else if (status == "ABORT")
                return new OnDemandResult() { Status = 1 };
            else if (status == "ERROR")
            {
                logger.Error("No Report created.");
                return new OnDemandResult() { Status = -1 };
            }
            else
                return new OnDemandResult() { Status = 0 };
        }

        private string GetResultFile(string taskId)
        {
            var resultFolder = Path.Combine(OnDemandConfig.WorkingDir, taskId, "JobResults");
            if (Directory.Exists(resultFolder))
            {
                var resultFiles = new DirectoryInfo(resultFolder).GetFiles("*.json", SearchOption.TopDirectoryOnly).ToList();
                var sortFiles = resultFiles.OrderBy(f => f.LastWriteTime).Reverse();
                return sortFiles.FirstOrDefault().FullName;
            }

            return null;
        }

        private JObject GetJsonObject(string taskId = null)
        {
            var resultFile = GetResultFile(taskId);
            if (File.Exists(resultFile))
            {
                logger.Debug($"json file {resultFile} found.");
                var json = File.ReadAllText(resultFile);
                return JsonConvert.DeserializeObject<JObject>(json);
            }

            logger.Error($"json file {resultFile} not found.");
            return null;
        }

        private string GetReportFile(string taskId)
        {
            try
            {
                var jobject = GetJsonObject(taskId);
                var path = jobject["reports"].FirstOrDefault()["paths"].FirstOrDefault().Value<string>() ?? null;
                return path;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private string GetStatus(string taskId)
        {
            try
            {
                var jobject = GetJsonObject(taskId);
                return jobject?.Property("status")?.Value?.Value<string>() ?? null;
            }
            catch(Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private string GetTaskId(string tempDir)
        {
            var jobject = GetJsonObject(tempDir);
            return jobject?.Property("taskId")?.Value.Value<string>() ?? null;
        }
        #endregion
    }
}