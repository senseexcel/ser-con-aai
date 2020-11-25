﻿namespace Ser.ConAai.Functions
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Web;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using NLog;
    using Q2g.HelperPem;
    using Q2g.HelperQlik;
    using Qlik.EngineAPI;
    using Ser.Api;
    using Ser.ConAai.Config;
    using Ser.ConAai.TaskObjects;
    using Ser.ConAai.Communication;
    #endregion

    public class StartFunction : BaseFunction
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        private readonly object threadObject = new object();
        #endregion

        #region Constructor
        public StartFunction(RuntimeOptions options) : base(options) { }
        #endregion

        #region Private Methods
        private SerConfig CreateEngineConfig(QlikRequest request, SessionInfo session)
        {
            logger.Debug($"Resolve script '{request.JsonScript}' for engine config...");
            var jsonStr = request.JsonScript;

            //Make full JSON for Engine
            logger.Debug("Auto replacement to normal json structure...");
            if (!jsonStr.ToLowerInvariant().Contains("\"reports\":"))
                jsonStr = $"\"reports\":[{jsonStr}]";

            if (!jsonStr.ToLowerInvariant().Contains("\"tasks\":"))
                jsonStr = $"\"tasks\":[{{{jsonStr}}}]";

            if (!jsonStr.Trim().StartsWith("{"))
                jsonStr = $"{{{jsonStr}";

            if (!jsonStr.Trim().EndsWith("}"))
                jsonStr = $"{jsonStr}}}";

            logger.Debug("Search for connections for the engine config...");
            dynamic serJsonConfig = JObject.Parse(jsonStr);
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
                        logger.Error($"No valid connection type '{connections.Type}'.");
                    var newUserConnections = new List<JToken>();
                    for (int i = 0; i < userConnections.Count; i++)
                    {
                        var mergeConnection = userConnections[i] as JObject;
                        var credType = Options.Config?.Connection?.Credentials?.Type ?? QlikCredentialType.JWT;
                        var connectorConnection = JObject.FromObject(CreateConnection(credType, session));
                        connectorConnection.Merge(mergeConnection, new JsonMergeSettings()
                        {
                            MergeNullValueHandling = MergeNullValueHandling.Ignore
                        });
                        newUserConnections.Add(connectorConnection);
                    }

                    report.connections = new JArray(newUserConnections);
                    if (report.distribute is JObject distribute)
                    {
                        var children = distribute?.Children().Children()?.ToList() ?? new List<JToken>();
                        foreach (dynamic child in children)
                        {
                            var connection = child.connections ?? null;
                            if (connection == null)
                                child.connections = new JArray(newUserConnections);
                            else if (connection?.ToString() == "@CONFIGCONNECTION@")
                                child.connections = new JArray(newUserConnections);
                        }
                    }

                    var privateKey = Options.Config?.Connection?.Credentials?.PrivateKey ?? null;
                    if (!String.IsNullOrEmpty(privateKey))
                    {
                        // For file access
                        lock (threadObject)
                        {
                            var path = HelperUtilities.GetFullPathFromApp(privateKey);
                            var crypter = new TextCrypter(path);
                            var value = report?.template?.outputPassword ?? null;
                            if (value == null)
                                value = report?.template?.outputpassword ?? null;
                            if (value != null)
                            {
                                string password = value.ToString();
                                if (value.Type == JTokenType.Boolean)
                                    password = password.ToLowerInvariant();
                                bool useBase64Password = report?.template?.useBase64Password ?? false;
                                if (useBase64Password == true)
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

        private SerConnection CreateConnection(QlikCredentialType type, SessionInfo session, string dataAppId = null)
        {
            try
            {
                logger.Debug("Create new connection.");
                var mainConnection = Options.Config.Connection;
                var token = Options.SessionHelper.Manager.GetToken(session.User, mainConnection, TimeSpan.FromMinutes(30));
                logger.Debug($"Bearer Token: {token}");

                var conn = new SerConnection()
                {
                    ServerUri = mainConnection.ServerUri,
                };

                switch (type)
                {
                    case QlikCredentialType.JWT:
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

        private byte[] DownloadFile(string relUrl, Cookie cookie)
        {
            using var webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.Cookie, $"{cookie.Name}={cookie.Value}");
            return webClient.DownloadData($"{Options.Config.Connection.ServerUri.AbsoluteUri}{relUrl}");
        }

        private Stream FindTemplatePath(SessionInfo session, SerTemplate template)
        {
            template.Input = HttpUtility.UrlDecode(template.Input);
            var result = HelperUtilities.NormalizeUri(template.Input);
            var templateUri = result.Item1;
            if (templateUri.Scheme.ToLowerInvariant() == "content")
            {
                var contentFiles = GetLibraryContent(session.QlikConn.CurrentApp, result.Item2);
                logger.Debug($"File count in content library: {contentFiles?.Count}");
                var modyPath = templateUri.AbsolutePath;
                modyPath = modyPath.Replace("(", "%28");
                modyPath = modyPath.Replace(")", "%29");
                var filterFile = contentFiles.FirstOrDefault(c => c.EndsWith(modyPath));
                if (filterFile != null)
                {
                    var data = DownloadFile(filterFile, session.Cookie);
                    template.Input = $"{Guid.NewGuid()}{Path.GetExtension(filterFile)}";
                    return new MemoryStream(data);
                }
                else
                    throw new Exception($"No file in content library found.");
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
                    throw new Exception($"No path in lib library found.");
                else
                {
                    template.Input = Uri.EscapeDataString(Path.GetFileName(connUrl));
                    return File.OpenRead(connUrl);
                }
            }
            else
            {
                throw new Exception($"Unknown Scheme in Filename Uri {template.Input}.");
            }
        }

        private List<string> GetLibraryContentInternal(IDoc app, string qName)
        {
            var libContent = app.GetLibraryContentAsync(qName).Result;
            return libContent.qItems.Select(u => u.qUrl).ToList();
        }

        private List<string> GetLibraryContent(IDoc app, string contentName = "")
        {
            try
            {
                var results = new List<string>();
                var readItems = new List<string>() { contentName };
                if (String.IsNullOrEmpty(contentName))
                {
                    //Search for all App Specific ContentLibraries
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

        private JObject AppendUploadGuids(JObject jobJson, List<Guid> guidList)
        {
            var jarray = new JArray();
            foreach (var guidItem in guidList)
                jarray.Add(guidItem.ToString());
            var uploadGuids = new JProperty("uploadGuids", jarray);
            jobJson.Last.AddAfterSelf(uploadGuids);
            return jobJson;
        }

        private void WaitForDataLoad(ManagedTask task, SerConnection configConn)
        {
            var scriptApp = configConn?.App;
            var timeout = configConn?.RetryTimeout ?? 0;
            var dataloadCheck = ScriptCheck.DataLoadCheck(Options.Config.Connection.ServerUri, scriptApp, task, timeout);
            if (dataloadCheck)
            {
                logger.Debug("Transfer script to the rest service...");
                var jobJson = task.JobScript.ToString();
                logger.Debug($"JSON-Script: {jobJson}");
                var createTaskResult = Options.RestClient.CreateTaskWithIdAsync(task.Id, jobJson).Result;
                if (createTaskResult.Success.Value)
                    logger.Info($"The reporting request was successfully transferred to the rest service. - OperationId: {createTaskResult?.OperationId}");
                else
                    throw new Exception($"The reporting request could not be transferred to the rest service. - Error: {createTaskResult?.Error}");
            }
            else
                logger.Debug("Dataload check failed.");
        }
        #endregion

        #region Public Methods
        public ManagedTask StartReportJob(QlikRequest request, ManagedTask newManagedTask)
        {
            try
            {
                logger.Debug("Create new job...");
                logger.Info($"Memory usage: {GC.GetTotalMemory(true)}");
                Options.Analyser?.ClearCheckPoints();
                Options.Analyser?.Start();
                Options.Analyser?.SetCheckPoint("CreateReports", "Start report generation");

                MappedDiagnosticsLogicalContext.Set("jobId", newManagedTask.Id.ToString());

                //Get Qlik session over jwt
                logger.Debug("Get cookie over JWT session...");
                newManagedTask.Session = Options.SessionHelper.GetSession(Options.Config.Connection, request);
                if (newManagedTask.Session == null)
                    throw new Exception("No session cookie generated (check qmc settings or connector config).");

                //Connect to Qlik app
                logger.Debug("Connecting to Qlik via websocket...");
                Options.Analyser?.SetCheckPoint("CreateReports", "Connect to Qlik");
                var fullConnectionConfig = new SerConnection
                {
                    App = request.AppId,
                    ServerUri = Options.Config.Connection.ServerUri,
                    Credentials = new SerCredentials()
                    {
                        Type = QlikCredentialType.SESSION,
                        Key = newManagedTask.Session.Cookie.Name,
                        Value = newManagedTask.Session.Cookie.Value
                    }
                };
                var qlikConnection = ConnectionManager.NewConnection(fullConnectionConfig, true);
                newManagedTask.Session.QlikConn = qlikConnection ?? throw new Exception("The web socket connection to qlik could not be established (Connector).");

                //Create full engine config
                logger.Debug("Create configuration for the engine...");
                Options.Analyser?.SetCheckPoint("CreateReports", "Gernerate Config Json");
                var newEngineConfig = CreateEngineConfig(request, newManagedTask.Session);

                //Remove emtpy Tasks without report infos
                newEngineConfig.Tasks.RemoveAll(t => t.Reports.Count == 0);

                foreach (var configTask in newEngineConfig.Tasks)
                {
                    if (configTask.Id == Guid.Empty)
                        configTask.Id = Guid.NewGuid();
                    
                    foreach (var configReport in configTask.Reports)
                    {
                        //Important: Add bearer connection as last connection item.
                        var firstConnection = configReport?.Connections?.FirstOrDefault() ?? null;
                        if (firstConnection != null)
                        {
                            logger.Debug("Create bearer connection.");
                            var newBearerConnection = CreateConnection(QlikCredentialType.JWT, newManagedTask.Session, firstConnection.App);
                            configReport.Connections.Add(newBearerConnection);
                        }

                        //Check app Id
                        var appList = Q2g.HelperQlik.Connection.PossibleApps;
                        var activeApp = appList.FirstOrDefault(a => a.qDocId == firstConnection.App);
                        if (activeApp == null)
                            throw new Exception($"The app id {firstConnection.App} was not found. Please check the app id or the security rules.");

                        //Read content from lib and content libary
                        logger.Debug("Get template data from qlik.");
                        if (configReport.Template != null)
                        {
                            var uploadsteam = FindTemplatePath(newManagedTask.Session, configReport.Template);
                            logger.Debug("Upload template data to rest service.");
                            var serfilename = Path.GetFileName(configReport.Template.Input);
                            var uploadResult = Options.RestClient.UploadAsync(serfilename, false, uploadsteam).Result;
                            if (uploadResult.Success.Value)
                            {
                                logger.Debug($"Upload {uploadResult.OperationId} successfully.");
                                newManagedTask.FileUploadIds.Add(uploadResult.OperationId.Value);
                            }
                            else
                                logger.Warn($"The Upload was failed. - Error: {uploadResult?.Error}");
                            uploadsteam.Close();
                        }
                        else
                        {
                            logger.Debug("No Template found. - Use alternative mode.");
                        }

                        // Perfomance analyser for the engine
                        configReport.General.UsePerfomanceAnalyzer = Options.Config.UsePerfomanceAnalyzer;
                    }
                }

                //Append upload ids on config
                var jobJson = JObject.FromObject(newEngineConfig);
                jobJson = AppendUploadGuids(jobJson, newManagedTask.FileUploadIds);
                newManagedTask.JobScript = jobJson;

                //Use the connector in the same App, than wait for data reload
                Options.Analyser?.SetCheckPoint("CreateReports", "Start connector reporting task");
                var scriptConnection = newEngineConfig?.Tasks?.SelectMany(s => s.Reports)
                ?.SelectMany(r => r.Connections)
                ?.FirstOrDefault(c => c.App == newManagedTask.Session.AppId) ?? null;

                //Wait for data load in single App mode
                WaitForDataLoad(newManagedTask, scriptConnection);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The reporting order could not be executed properly.");
                newManagedTask.Endtime = DateTime.Now;
                newManagedTask.Status = -1;
                newManagedTask.Error = ex;
                Options.SessionHelper.Manager.MakeSocketFree(newManagedTask?.Session ?? null);
            }
            return newManagedTask;
        }
        #endregion
    }
}