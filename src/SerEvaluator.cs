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
            DOWNLOAD = 3
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
                                 new Parameter() { Name = "TaskID", DataType = DataType.String }
                             },
                             ReturnType = DataType.String
                        },
                        new FunctionDefinition()
                        {
                             FunctionId = 3,
                             FunctionType = FunctionType.Scalar,
                             Name = SerFunction.DOWNLOAD.ToString(),
                             Params =
                             {
                                 new Parameter() { Name = "ReportName", DataType = DataType.String }
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

                if (requestStream.MoveNext().Result == false)
                    throw new Exception("No parameter found.");

                var commonHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<CommonRequestHeader>();
                logger.Info($"request from user: {commonHeader.UserId} for AppId: {commonHeader.AppId}");
                var userParameter = new UserParameter()
                {
                    AppId = commonHeader.AppId,
                    DomainUser = new DomainUser(commonHeader.UserId),
                };

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });
   
                var result = String.Empty;
                logger.Debug($"Function id: {functionRequestHeader.FunctionId}");
                var row = requestStream?.Current?.Rows.FirstOrDefault() ?? null;
                if (functionRequestHeader.FunctionId == (int)SerFunction.CREATE)
                {
                    userParameter.TemplateFileName = GetParameterValue(0, row);
                    logger.Debug($"Template path: {userParameter.TemplateFileName}");
                    userParameter.SaveFormats = GetParameterValue(1, row);
                    logger.Debug($"SaveFormat: {userParameter.SaveFormats}");
                    userParameter.UseUserSelesction = Boolean.TryParse(GetParameterValue(2, row), out var boolResult);
                    logger.Debug($"UseSelection: {userParameter.UseUserSelesction}");
                    result = CreateReport(userParameter);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.STATUS)
                {
                    var taskId = GetParameterValue(0, row);
                    logger.Debug($"TaskId: {taskId}");
                    result = Status(taskId, userParameter);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.DOWNLOAD)
                {
                    var taskId = GetParameterValue(0, row);
                    logger.Debug($"TaskId: {taskId}");
                    var domainUser = new DomainUser(commonHeader.UserId);
                    var doc = GetFirstUserReport(domainUser);
                    if (doc == null)
                        logger.Error("No Download Document found.");
                    else
                        result = $"{OnDemandConfig.Server}{doc?.References.FirstOrDefault().ExternalPath}";
                    logger.Debug($"Download url {result}");
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
                await responseStream.WriteAsync(GetResult("-1"));
            }
            finally
            {
                LogManager.Flush();
            }
        }
        #endregion

        #region Private Functions
        private string CreateReport(UserParameter parameter)
        {
            try
            {
                var tplPath = parameter.TemplateFileName;
                if (!File.Exists(tplPath))
                    tplPath = Path.Combine(OnDemandConfig.TemplateFolder, tplPath);

                if (!File.Exists(tplPath))
                    throw new Exception($"Template path {tplPath} not exits.");

                //Copy Template
                var workDir = OnDemandConfig.WorkingDir;
                var taskId = Guid.NewGuid().ToString();
                logger.Debug($"New Task-ID: {taskId}");
                var currentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {currentWorkingDir}");
                Directory.CreateDirectory(currentWorkingDir);
                var tplCopyPath = Path.Combine(currentWorkingDir, Path.GetFileName(tplPath));
                File.Copy(tplPath, tplCopyPath, true);

                //Get a session
                var cookie = sessionManager.GetSession(new Uri(OnDemandConfig.Server), parameter.DomainUser, OnDemandConfig.CookieName,
                                                        OnDemandConfig.VirtualProxyPath, OnDemandConfig.Certificate);
                //Save config for SER engine
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                SaveSerConfig(savePath, tplCopyPath, cookie, parameter);

                //Start SER Engine as Process
                logger.Debug($"Start Engine \"{currentWorkingDir}\"");
                var serProcess = new Process();
                serProcess.StartInfo.FileName = OnDemandConfig.SerEnginePath;
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();

                //wait for finish and upload
                var uploadThread = new Thread(() => Upload(taskId, currentWorkingDir, parameter))
                {
                    IsBackground = true
                };
                uploadThread.Start();

                //TaskId
                return taskId;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The report could not created.");
                return "-1";
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

        private BundledRows GetResult(string resultValue)
        {
            var resultBundle = new BundledRows();
            var resultRow = new Row();
            resultRow.Duals.Add(new Dual { StrData = resultValue });
            resultBundle.Rows.Add(resultRow);
            return resultBundle;
        }

        private void SaveSerConfig(string savePath, string templatePath, Cookie cookie, UserParameter parameter)
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
                        FileName = templatePath,
                        SaveFormats = parameter.SaveFormats,
                        ReportName = Path.GetFileNameWithoutExtension(templatePath),
                    },
                    Connection = new SerConnection()
                    {
                        App = parameter.AppId,
                        ConnectUri = $"{OnDemandConfig.Server}/{OnDemandConfig.VirtualProxyPath}",
                        VirtualProxyPath = OnDemandConfig.VirtualProxyPath,
                        Credentials = new SerCredentials()
                        {
                            Type = QlikCredentialType.SESSION,
                            Key = cookie.Name,
                            Value = cookie.Value,
                        }
                    }
                };

                var appConfig = new SerConfig() { Tasks = new List<SerTask> { task } };
                var json = JsonConvert.SerializeObject(appConfig);
                File.WriteAllText(savePath, json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Config for SER-Engine not saved.");
            }
        }

        private void Upload(string taskId, string currentWorkingDir, UserParameter parameter)
        {
            try
            {
                var status = "0";
                while (status != "100")
                {
                    Thread.Sleep(250);
                    status = Status(taskId, parameter);
                    if (status == "-1")
                        return;
                }

                //Rename file name
                var reportFile = GetReportFile(taskId);
                var renamePath = Path.Combine(Path.GetDirectoryName(reportFile), $"{OnDemandConfig.ReportName}.{parameter.SaveFormats}");
                File.Move(reportFile, renamePath);

                //Upload Shared Content
                var qlikHub = new QlikQrsHub(new Uri(OnDemandConfig.HubConnect))
                {
                    UserId = parameter?.DomainUser?.UserId ?? null,
                    UserDirectory = parameter?.DomainUser?.UserDirectory ?? null,
                };
                var doc = GetFirstUserReport(parameter.DomainUser);
                if (doc == null)
                    qlikHub.Create(OnDemandConfig.ReportName, renamePath, $"Created by SER OnDemand Connector.");
                else
                    qlikHub.Update(OnDemandConfig.ReportName, renamePath);
                logger.Debug($"upload file {reportFile}");

                //Wait for Status Success 
                Thread.Sleep(1000);

                //Delete job files after upload
                SoftDelete(currentWorkingDir);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
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

        private string Status(string taskId, UserParameter parameter)
        {
            var status = GetStatus(taskId);
            logger.Debug($"Report status {status}");
            if (status == "SUCCESS")
                return "100";
            else if (status == "ABORT")
                return "1";
            else if (status == "ERROR")
            {
                logger.Error("No Report created.");
                return "-1";
            }
            else
                return "0";
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

        private HubInfo GetFirstUserReport(DomainUser user)
        {
            var qlikHub = new QlikQrsHub(new Uri(OnDemandConfig.HubConnect))
            {
                UserId = user?.UserId ?? null,
                UserDirectory = user?.UserDirectory ?? null,
            };

            return qlikHub?.GetAllSharedContent($"Name eq '{OnDemandConfig?.ReportName}'")?.Where(d => d?.Owner?.UserId == user?.UserId &&
                                                d?.Owner?.UserDirectory == user?.UserDirectory).FirstOrDefault() ?? null;
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