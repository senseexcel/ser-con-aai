#region License
/*
Copyright (c) 2018 Konrad Mattheis und Martin Berthold
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
#endregion

namespace SerConAai
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
    using Q2gHelperQrs;
    using SerApi;
    using Hjson;
    using System.Net.Http;
    #endregion

    public class SerEvaluator : ConnectorBase, IDisposable
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Enumerator
        public enum SerFunction
        {
            CREATE = 1,
            STATUS = 2,
            ABORT = 3,
            START = 4
        }
        #endregion

        #region Properties & Variables
        private SerOnDemandConfig OnDemandConfig;
        private SessionManager sessionManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            OnDemandConfig = config;
            sessionManager = new SessionManager();
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
                        new FunctionDefinition()
                        {
                             FunctionId = 1,
                             FunctionType = FunctionType.Scalar,
                             Name = SerFunction.CREATE.ToString(),
                             Params =
                             {
                                new Parameter() { Name = "TemplateFilename", DataType = DataType.String },
                                new Parameter() { Name = "OutputFormat", DataType = DataType.String },
                                new Parameter() { Name = "UseSelection", DataType = DataType.String },
                             },
                             ReturnType = DataType.String,
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
                        },
                        new FunctionDefinition()
                        {
                            FunctionId = 4,
                            FunctionType = FunctionType.Scalar,
                            Name = SerFunction.START.ToString(),
                            Params =
                            {
                                new Parameter() { Name = "Script", DataType = DataType.String }
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
                logger.Debug($"DomainUser: {domainUser.UserId.ToString()}\\{domainUser.UserDirectory.ToString()}");

                var userParameter = new UserParameter()
                {
                    AppId = commonHeader.AppId,
                    DomainUser = domainUser,
                };

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });
   
                logger.Debug($"Function id: {functionRequestHeader.FunctionId}");
                var row = GetParameter(requestStream);
                var result = new OnDemandResult() { Status = -1 };
                if (functionRequestHeader.FunctionId == (int)SerFunction.CREATE)
                {
                    userParameter.TemplateFileName = GetParameterValue(0, row);
                    logger.Debug($"Template path: {userParameter.TemplateFileName}");
                    userParameter.SaveFormats = GetParameterValue(1, row);
                    logger.Debug($"SaveFormat: {userParameter.SaveFormats}");
                    userParameter.UseUserSelesction = GetBoolean(GetParameterValue(2, row));
                    logger.Debug($"UseSelection: {userParameter.UseUserSelesction}");
                    result = CreateReport(userParameter, true);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.START)
                {
                    //Path und Script?
                    var json = GetParameterValue(0, row);
                    json = HjsonValue.Parse(json).ToString();
                    var jsonSerConfig = JsonConvert.DeserializeObject<SerConfig>(json.ToString());
                    if (jsonSerConfig != null)
                    {
                        logger.Debug("Json config is valid.");
                        result = CreateReport(userParameter, false, json);
                    }
                    else
                    {
                        throw new Exception("Json config is invalid.");
                    }
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.STATUS)
                {
                    //Status -1=Fail 1=Running, 2=Success, 3=DeleverySuccess, 4=StopSuccess, 5=Download
                    var taskId = GetParameterValue(0, row)?.Replace("\"","");
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.Connection.ConnectUri), domainUser, taskId);
                    if (session == null)
                    {
                        logger.Error($"No existing session with id {taskId} found.");
                        result = new OnDemandResult() { Status = -1 };
                    }

                    if (session.DownloadLink != null)
                        result = new OnDemandResult() { Status = 5, Link = session.DownloadLink };
                    else
                        result = new OnDemandResult() { Status = session.Status };
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.ABORT)
                {
                    var taskId = GetParameterValue(0, row)?.Replace("\"", "");
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.Connection.ConnectUri), domainUser, taskId);
                    if (session == null)
                        throw new Exception("No existing session found.");
                    var process = Process.GetProcessById(session.ProcessId);
                    if (!process.HasExited)
                    {
                        process?.Kill();
                        Thread.Sleep(1000);
                    }
                    SoftDelete($"{OnDemandConfig.WorkingDir}\\{taskId}");
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
        private OnDemandResult CreateReport(UserParameter parameter, bool onDemandMode, string json = null)
        {
            try
            {
                var taskId = Guid.NewGuid().ToString();
                logger.Debug($"New Task-ID: {taskId}");
                var workDir = OnDemandConfig.WorkingDir;
                var currentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {currentWorkingDir}");
                Directory.CreateDirectory(currentWorkingDir);

                //Prüfen auf hier running task!!!


                //Caution: Personal//Me => Desktop Mode
                if (parameter.DomainUser.UserId == "sa_scheduler" &&
                    parameter.DomainUser.UserDirectory == "INTERNAL")
                {
                    //In Doku mit aufnehmen / Security rule für Task User ser_scheduler
                    var tmpsession = sessionManager.GetSession(new Uri(OnDemandConfig.Connection.ConnectUri), 
                                                               new DomainUser("INTERNAL\\ser_scheduler"),
                                                               OnDemandConfig.Connection, taskId);
                    var qrshub = new QlikQrsHub(new Uri(GetHost()), tmpsession.Cookie);
                    var result = qrshub.GetAppContent(new Uri($"https://nb-fc-208000/ser/qrs/app/{parameter.AppId}")).Result;
                    var hubInfo = JsonConvert.DeserializeObject<HubInfo>(result);
                    if (hubInfo == null)
                        throw new Exception($"No app owner for app id {parameter.AppId} found.");
                    parameter.DomainUser = new DomainUser($"{hubInfo.Owner.UserDirectory}\\{hubInfo.Owner.UserId}");
                }

                //Get a session
                var session = sessionManager.GetSession(new Uri(OnDemandConfig.Connection.ConnectUri), parameter.DomainUser,
                                                        OnDemandConfig.Connection, taskId);
                session.User = parameter.DomainUser;
                parameter.ConnectCookie = session?.Cookie;

                var tplPath = parameter.TemplateFileName;
                if (tplPath == null && onDemandMode == false)
                {
                    //ser standard config
                    json = GetNewJson(parameter, json, currentWorkingDir, session.Cookie);
                }
                else
                {
                    //Template holen
                    var host = GetHost();
                    var contentFiles = GetLibraryContent(host, parameter.AppId, session.Cookie, true);
                    var relUrl = contentFiles.FirstOrDefault(f => f.EndsWith(parameter.TemplateFileName));
                    var downloadPath = DownloadFile(relUrl, currentWorkingDir, session.Cookie);

                    //generate ser config for ondemand
                    json = GetNewSerConfig(downloadPath, parameter);
                }

                //Save config for SER engine
                Directory.CreateDirectory(currentWorkingDir);
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                File.WriteAllText(savePath, json);

                //Start SER Engine as Process
                logger.Debug($"Start Engine \"{currentWorkingDir}\"");
                var serProcess = new Process();
                serProcess.StartInfo.FileName = OnDemandConfig.SerEnginePath;
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();
                session.ProcessId = serProcess.Id;

                if (onDemandMode)
                {
                    //wait for finish and upload
                    var uploadThread = new Thread(() => Upload(taskId, currentWorkingDir, parameter))
                    {
                        IsBackground = true
                    };
                    uploadThread.Start();
                }
                else
                {
                    var statusThread = new Thread(() => CheckStatus(taskId, currentWorkingDir, parameter))
                    {
                        IsBackground = true
                    };
                    statusThread.Start();
                }

                return new OnDemandResult() { TaskId = taskId };
            }
            catch (Exception ex)
            {
                throw new Exception("The report could not be created.", ex);
            }
        }

        private string GetNewJson(UserParameter parameter, string json, string workdir, Cookie cookie)
        {
            try
            {
                //dynamic gb = task;
                //var appID2 = gb.connection.app.ToString();

                var host = GetHost();
                logger.Debug($"Websocket host: {host}");
                var serConfig = JObject.Parse(HjsonValue.Parse(json).ToString());
                var tasks = serConfig["tasks"].ToList();
                foreach (var task in tasks)
                {
                    var scriptCon = JObject.Parse(task["connection"].ToString());
                    OnDemandConfig.Connection.Credentials.Value = cookie.Value;
                    var configCon = JObject.Parse(JsonConvert.SerializeObject(OnDemandConfig.Connection, Formatting.None,
                                                  new JsonSerializerSettings
                                                  {
                                                     NullValueHandling = NullValueHandling.Ignore
                                                  }));
                    configCon.Merge(scriptCon);
                    var currentConnection = configCon.Value<JObject>();
                    task["connection"] = currentConnection;
                    var value = task["evaluate"]["hub"]["connection"].Value<string>();
                    if(value == "@CONFIGCONNECTION@")
                      task["evaluate"]["hub"]["connection"] = currentConnection;

                    var appId = task["connection"]["app"].ToString();
                    var fileName = task["template"]["input"].ToString();
                    if (fileName.ToLowerInvariant().StartsWith("content://"))
                    {
                        var contentFiles = new List<string>();
                        var contentUri = new Uri(fileName);
                        if (String.IsNullOrEmpty(contentUri.Host))
                        {
                            contentUri = new Uri($"content://{appId}{contentUri.AbsolutePath}");
                            contentFiles = GetLibraryContent(GetNativeHost(host), appId, cookie, true);
                        }
                        else
                            contentFiles = GetLibraryContent(GetNativeHost(host), appId, cookie, false, contentUri.Host);

                        logger.Debug($"File count in content library: {contentFiles?.Count}");
                        var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(contentUri.AbsolutePath));
                        if (filterFile != null)
                        {
                            var savePath = DownloadFile(filterFile, workdir, cookie);
                            task["template"]["input"] = Path.GetFileName(savePath);
                            logger.Debug($"Filename {fileName} in content library found.");
                        }
                        else
                            logger.Warn($"No file in content library found.");
                    }
                    else if (fileName.ToLowerInvariant().StartsWith("lib://"))
                    {
                        var libUri = new Uri(fileName);
                        if (String.IsNullOrEmpty(libUri.Host))
                            throw new Exception("Unknown Name of the lib connection.");

                        var connections = GetConnections(host, appId, cookie);
                        var libResult = connections.FirstOrDefault(n => n["qName"].ToString().ToLowerInvariant() == libUri.Host);
                        var libPath = libResult["qConnectionString"].ToString();
                        var relPath = libUri.LocalPath.TrimStart(new char[] { '\\', '/' }).Replace("/", "\\");
                        task["template"]["input"] = $"{libPath}{relPath}";
                    }
                    else
                    {
                        throw new Exception($"Unknown Sheme in Filename Uri {fileName}.");
                    }
                }
                return serConfig.ToString();
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"The propertys for ser config could not be set. Json: {json}");
                return null;
            }
        }

        private string GetNativeHost(string host)
        {
            return host.Replace("https://", "").Replace("http://", "");
        }

        private string GetHost(bool withProxy = true)
        {
            var url = $"{OnDemandConfig.Connection.ConnectUri}";
            if (withProxy == false)
                return url;
            if (!String.IsNullOrEmpty(OnDemandConfig?.Connection?.VirtualProxyPath))
                url += $"/{OnDemandConfig.Connection.VirtualProxyPath}";
            return url;
        }

        private string DownloadFile(string relUrl, string workdir, Cookie cookie)
        {
            var savePath = Path.Combine(workdir, Path.GetFileName(relUrl));
            var url = $"{GetHost()}{relUrl}";
            var webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
            webClient.DownloadFile(url, savePath);
            return savePath;
        }

        private List<JToken> GetConnections(string host, string appId, Cookie cookie)
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

        private List<string> GetLibraryContent(string host, string appId, Cookie cookie, bool qAppSpecific = true, string qName = null)
        {
            try
            {
                var results = new List<string>();
                var qlikWebSocket = new QlikWebSocket(host, cookie);
                var isOpen = qlikWebSocket.OpenSocket();
                var response = qlikWebSocket.OpenDoc(appId);
                var handle = response["result"]["qReturn"]["qHandle"].ToString();
                response = qlikWebSocket.GetContentLibraries(handle);
                var qItems = response["result"]["qList"]["qItems"].ToList();
                if (qAppSpecific == true)
                    qItems = qItems.Where(s => s["qAppSpecific"]?.Value<bool>() == true).ToList();

                if (String.IsNullOrEmpty(qName))
                {
                    foreach (var item in qItems)
                    {
                        var name = item["qName"].ToString();
                        var qUrls = GetLibraryContentInternal(qlikWebSocket, handle, name);
                        results.AddRange(qUrls);
                    }
                }
                else
                {
                    var qUrls = GetLibraryContentInternal(qlikWebSocket, handle, qName);
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
            resultRow.Duals.Add(new Dual { StrData = JsonConvert.SerializeObject(result, settings) });
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
                        FileName = Path.GetFileName(templatePath),
                        SaveFormats = parameter.SaveFormats,
                        ReportName = Path.GetFileNameWithoutExtension(templatePath),
                    },
                    Connection = new SerConnection()
                    {
                        App = parameter.AppId,
                        ConnectUri = GetHost(),
                        VirtualProxyPath = OnDemandConfig.Connection.VirtualProxyPath,
                        Credentials = new SerCredentials()
                        {
                            Type = QlikCredentialType.SESSION,
                            Key = parameter.ConnectCookie.Name,
                            Value = parameter.ConnectCookie.Value,
                        }
                    }
                };

                var appConfig = new SerConfig() { Tasks = new List<SerTask> { task } }; 
                return JsonConvert.SerializeObject(appConfig);
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
            var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.Connection.ConnectUri), parameter.DomainUser, taskId);
            session.Status = 1;
            while (status != 2)
            {
                Thread.Sleep(250);
                var result = Status(taskId);
                status = result.Status;
                if (status == -1)
                    break;
            }

            session.Status = status;
            if (status != 2)
                return;

            status = StartDeliveryTool(currentWorkingDir);
            if (status == 3)
            {
                SoftDelete(currentWorkingDir);
                sessionManager.DeleteSession(new Uri(OnDemandConfig.Connection.ConnectUri), parameter.DomainUser, taskId);
            }
            session.Status = status;
        }

        private void Upload(string taskId, string currentWorkingDir, UserParameter parameter)
        {
            try
            {
                var status = 0;
                while (status != 100)
                {
                    Thread.Sleep(250);
                    var result = Status(taskId);
                    status = result.Status;
                    if (status == -1)
                        return;
                }

                //Rename file name
                var reportFile = GetReportFile(taskId);
                var renamePath = Path.Combine(Path.GetDirectoryName(reportFile), $"{OnDemandConfig.ReportName}.{parameter.SaveFormats}");
                File.Move(reportFile, renamePath);

                //Upload Shared Content
                var qlikHub = new QlikQrsHub(new Uri(GetHost()), parameter.ConnectCookie);
                var hubInfo = GetFirstUserReport(parameter.DomainUser, parameter.ConnectCookie);
                if (hubInfo == null)
                {
                    var createRequest = new HubCreateRequest()
                    {
                        Name = OnDemandConfig.ReportName,
                        Description = $"Created by SER OnDemand Connector.",
                        Data = GetContentData(renamePath),
                    };
                    qlikHub.CreateSharedContentAsync(createRequest).Wait();
                    logger.Debug($"upload new file {reportFile} - Create");
                }
                else
                {
                    var updateRequest = new HubUpdateRequest()
                    {
                        Info = hubInfo,
                        Data = GetContentData(renamePath),
                    };
                    qlikHub.UpdateSharedContentAsync(updateRequest).Wait();
                    logger.Debug($"upload new file {reportFile} - Update");
                }

                //Wait for Status Success 
                Thread.Sleep(1000);

                //Download Url
                hubInfo = GetFirstUserReport(parameter.DomainUser, parameter.ConnectCookie);
                if (hubInfo == null)
                    logger.Debug("No Document uploaded.");
                else
                {
                    var url = $"{OnDemandConfig.Connection.ConnectUri}{hubInfo?.References.FirstOrDefault().ExternalPath}";
                    logger.Debug($"Set Download Url {url}");
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.Connection.ConnectUri), parameter.DomainUser, taskId);
                    session.DownloadLink = url;
                }

                //Delete job files after upload
                SoftDelete(currentWorkingDir);
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

        private HubContentData GetContentData(string fullname)
        {
            var contentData = new HubContentData()
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

        private int StartDeliveryTool(string workdir)
        {
            try
            {
                var serDeliveryProc = new Process();
                serDeliveryProc.StartInfo.FileName = OnDemandConfig.DeliveryToolPath;
                serDeliveryProc.StartInfo.Arguments = $"{workdir}\\JobResults"; //without files jail
                serDeliveryProc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serDeliveryProc.Start();
                serDeliveryProc.WaitForExit();
                return 3;
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

        private HubInfo GetFirstUserReport(DomainUser user, Cookie cookie)
        {
            var qlikHub = new QlikQrsHub(new Uri(GetHost()), cookie);
            var selectRequest = new HubSelectRequest()
            {
                Filter = HubSelectRequest.GetNameFilter(OnDemandConfig?.ReportName),
            };

            var results = qlikHub.GetSharedContentAsync(selectRequest).Result;
            var result = results?.Where(d => d?.Owner?.UserId == user?.UserId && 
                                     d?.Owner?.UserDirectory == user?.UserDirectory)?.FirstOrDefault() ?? null;
            return result;
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