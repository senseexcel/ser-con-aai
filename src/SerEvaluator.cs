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
    using System.Net.Sockets;
    using System.IO;
    using System.Net.NetworkInformation;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using System.Text;
    using System.Security.Cryptography;
    using System.Collections.Concurrent;
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using System.Net.Http;
    using Newtonsoft.Json.Linq;
    using System.IdentityModel.Tokens;
    using System.Security.Claims;
    using Microsoft.IdentityModel.Tokens;
    using System.IdentityModel.Tokens.Jwt;
    using Q2gHelperPem;
    using Q2gHelperQrs;
    using SerApi;
    #endregion

    public class SerEvaluator : ConnectorBase, IDisposable
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        private SerOnDemandConfig OnDemandConfig;
        private QlikQrsHub QlikHub;
        private SessionManager sessionManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            OnDemandConfig = config;
            QlikHub = new QlikQrsHub(new Uri(OnDemandConfig.HubConnect));
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
                             Name = "Create",
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
                             Name = "Status",
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
                             Name = "Download",
                             Params =
                             {
                                 new Parameter() { Name = "TaskID", DataType = DataType.String }
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

                Thread.Sleep(OnDemandConfig.Wait);
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
                OnDemandConfig.CurrentAppId = commonHeader.AppId;
                var domainUser = GetFormatedUserId(commonHeader.UserId);
                OnDemandConfig.DomainUser = new DomainUser(commonHeader.UserId);

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });
   
                var result = String.Empty;
                logger.Debug($"Function id: {functionRequestHeader.FunctionId}");
                var row = requestStream?.Current?.Rows.FirstOrDefault() ?? null;
                if (functionRequestHeader.FunctionId == 1)
                {
                    OnDemandConfig.TemplateFileName = GetParameterValue(0, row);
                    logger.Debug($"Template path: {OnDemandConfig.TemplateFileName}");
                    OnDemandConfig.SaveFormats = GetParameterValue(1, row);
                    logger.Debug($"SaveFormat: {OnDemandConfig.SaveFormats}");
                    OnDemandConfig.UseUserSelesction = Boolean.TryParse(GetParameterValue(2, row), out var boolResult);
                    logger.Debug($"UseSelection: {OnDemandConfig.UseUserSelesction}");
                    OnDemandConfig.DownloadUrl = null;
                    result = CreateReport();
                }
                else if (functionRequestHeader.FunctionId == 2)
                {
                    var taskId = GetParameterValue(0, row);
                    logger.Debug($"TaskId: {taskId}");
                    result = Status(taskId);
                }
                else if (functionRequestHeader.FunctionId == 3)
                {
                    var taskId = GetParameterValue(0, row);
                    logger.Debug($"TaskId: {taskId}");
                    result = OnDemandConfig.DownloadUrl;
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
        private string CreateReport()
        {
            try
            {
                var tplPath = OnDemandConfig.TemplateFileName;
                if (!File.Exists(tplPath))
                    tplPath = Path.Combine(OnDemandConfig.TemplateFolder, tplPath);

                if (!File.Exists(tplPath))
                    throw new Exception($"Template path {tplPath} not exits.");

                //Copy Template
                var workDir = OnDemandConfig.WorkingDir;
                var taskId = Guid.NewGuid().ToString();
                OnDemandConfig.CurrentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {OnDemandConfig.CurrentWorkingDir}");
                Directory.CreateDirectory(OnDemandConfig.CurrentWorkingDir);
                var tplCopyPath = Path.Combine(OnDemandConfig.CurrentWorkingDir, Path.GetFileName(tplPath));
                File.Copy(tplPath, tplCopyPath, true);

                //Get a session
                var cookie = sessionManager.GetSession(new Uri(OnDemandConfig.Server), OnDemandConfig.DomainUser, OnDemandConfig.CookieName,
                                                        OnDemandConfig.VirtualProxyPath, OnDemandConfig.Certificate);
                logger.Debug($"Session: {cookie?.Name} - {cookie?.Value}");

                //Save config for SER engine
                var savePath = Path.Combine(OnDemandConfig.CurrentWorkingDir, "job.json");
                SaveSerConfig(savePath, tplCopyPath, cookie);

                //Start SER Engine as Process
                var serProcess = new Process();
                serProcess.StartInfo.FileName = OnDemandConfig.SerEnginePath;
                serProcess.StartInfo.Arguments = $"--workdir \"{OnDemandConfig.CurrentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();

                //wait for finish and upload
                var uploadThread = new Thread(() => Upload(taskId))
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

        private string GetFormatedUserId(string userIdStr)
        {
            var split = userIdStr.Split(';');
            var userDirectory = split[0].Split('=');
            if (userDirectory.Length == 1)
                return userDirectory[0];
            else
            {
                var userId = split[1].Split('=');
                return $"{userDirectory.ElementAtOrDefault(1).Trim()}\\{userId.ElementAtOrDefault(1).Trim()}";
            }
        }

        private void SaveSerConfig(string savePath, string templatePath, Cookie cookie)
        {
            var task = new SerTask()
            {
                General = new SerGeneral()
                {
                    UseUserSelections = OnDemandConfig.UseUserSelesction,
                },
                Template = new SerTemplate()
                {
                    FileName = templatePath,
                    SaveFormats = OnDemandConfig.SaveFormats,
                    ReportName = Path.GetFileNameWithoutExtension(templatePath),
                },
                Connection = new SerConnection()
                {
                    App = OnDemandConfig.CurrentAppId,
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

        private void Upload(string taskId)
        {
            try
            {
                var status = "0";
                while (status != "100")
                {
                    Thread.Sleep(250);
                    status = Status(taskId);
                    if (status == "-1")
                        return;
                }
                 
                //Rename file name
                var reportFile = GetReportFile(taskId);
                var renamePath = Path.Combine(Path.GetDirectoryName(reportFile), $"{OnDemandConfig.ReportName}.{OnDemandConfig.SaveFormats}");
                File.Move(reportFile, renamePath);

                //Upload Shared Content
                QlikHub.UserId = OnDemandConfig?.DomainUser?.UserId ?? null;
                QlikHub.UserDirectory = OnDemandConfig?.DomainUser?.UserDirectory ?? null;
                var name = Path.GetFileNameWithoutExtension(OnDemandConfig.ReportName);
                var doc = QlikHub.GetFirstSharedContent(name);
                if (doc == null)
                    QlikHub.Create(name, renamePath, $"{taskId} - Created by SERConnector.");
                else
                    QlikHub.Update(name, renamePath);
                logger.Debug($"upload file {reportFile}");

                //Delete job files after upload
                SoftDelete(OnDemandConfig.CurrentWorkingDir);

                //Download Url
                doc = QlikHub.GetFirstSharedContent(name);
                if (doc == null)
                    logger.Error("No Download Document found.");
                else
                    OnDemandConfig.DownloadUrl = $"{OnDemandConfig.Server}{doc?.References.FirstOrDefault().ExternalPath}";
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

        private string Status(string taskId)
        {
            if (OnDemandConfig.DownloadUrl != null)
                return "100";

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