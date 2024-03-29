﻿namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.IO;
    using Grpc.Core;
    using Newtonsoft.Json;
    using NLog;
    using Ser.Api;
    using Q2g.HelperQrs;
    using Qlik.Sse;
    using static Qlik.Sse.Connector;
    using Ser.Diagnostics;
    using Ser.Gerneral;
    using Ser.ConAai.Functions;
    using Ser.ConAai.Config;
    using Ser.ConAai.TaskObjects;
    using Ser.ConAai.Communication;
    using Ser.Engine.Rest.Client;
    using AG.Renderer.Rest.Client;
    #endregion

    public class ConnectorWorker : ConnectorBase
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Enumerator
        public enum ConnectorFunction
        {
            START = 1,
            STATUS = 2,
            STOP = 3,
            RESULT = 4
        }
        #endregion

        #region Properties & Variables
        public RuntimeOptions RuntimeOptions { get; }
        #endregion

        #region Connstructor
        public ConnectorWorker(ConnectorConfig config, CancellationTokenSource cancellation)
        {
            PerfomanceAnalyser analyser = null;
            if (config.UsePerfomanceAnalyzer)
            {
                logger.Info("Use perfomance analyser.");
                analyser = new PerfomanceAnalyser(new AnalyserOptions()
                {
                    AnalyserFolder = Path.GetDirectoryName(SystemGeneral.GetLogFileName("file"))
                });
            }

            var restClient = new ReportingRestApiClient(new Uri(config?.RestServiceUrl?.TrimEnd('/')), config.RestTimeout);
            RuntimeOptions = new RuntimeOptions()
            {
                Config = config,
                SessionHelper = new SessionHelper(),
                RestClient = restClient,
                Analyser = analyser,
                Cancellation = cancellation,
                TaskPool = new ManagedTaskPool()
            };
            RuntimeOptions.TaskPool.Run(RuntimeOptions);

            var rendererClient = new ReportingRendererApiClient(new Uri(config?.RendererServiceUrl?.TrimEnd('/')), config.RestTimeout);
            var rendererVersion = rendererClient.GetVersion();
            logger.Info($"Renderer Version: {rendererVersion}");
            config.PackageVersions.Add($"AnalyticsGate Renderer: {rendererVersion}");
        }
        #endregion

        #region Private Methods
        private static Row GetParameter(IAsyncStreamReader<BundledRows> requestStream)
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

        private static string GetParameterValue(int index, Row row)
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

        private static BundledRows GetResult(object result)
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
        #endregion

        #region Public Methods
        public void CleanupOldFiles()
        {
            logger.Debug("Cleanup old files...");
            Task.Delay(250).ContinueWith((_) => RuntimeOptions.RestClient.Delete());
        }

        public void RestServiceHealthCheck()
        {
            try
            {
                logger.Debug("Check connection to rest service...");
                var healthResult = RuntimeOptions.RestClient.HealthStatus();
                logger.Debug($"Health result is '{healthResult}'...");
                logger.Info($"The communication with rest service '{RuntimeOptions.Config.RestServiceUrl}' was successfully.");
            }
            catch (Exception ex)
            {
                throw new Exception($"The connection to the rest service '{RuntimeOptions.Config.RestServiceUrl}' could not be established.", ex);
            }
        }

        public override Task<Capabilities> GetCapabilities(Empty request, ServerCallContext context)
        {
            try
            {
                logger.Info($"The method 'GetCapabilities' is called...");

                return Task.FromResult(new Capabilities
                {
                    PluginVersion = RuntimeOptions.Config.AppVersion,
                    PluginIdentifier = RuntimeOptions.Config.AppName,
                    AllowScript = false,
                    Functions =
                    {
                        new FunctionDefinition
                        {
                            FunctionId = 1,
                            FunctionType = FunctionType.Scalar,
                            Name = nameof(ConnectorFunction.START),
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
                             Name = nameof(ConnectorFunction.STATUS),
                             Params =
                             {
                                new Parameter { Name = "TaskId", DataType = DataType.String },
                             },
                             ReturnType = DataType.String
                        },
                        new FunctionDefinition
                        {
                            FunctionId = 3,
                            FunctionType = FunctionType.Scalar,
                            Name = nameof(ConnectorFunction.STOP),
                            Params =
                            {
                               new Parameter { Name = "TaskId", DataType = DataType.String },
                            },
                            ReturnType = DataType.String
                        },
                        new FunctionDefinition
                        {
                            FunctionId = 4,
                            FunctionType = FunctionType.Scalar,
                            Name = nameof(ConnectorFunction.RESULT),
                            Params =
                            {
                               new Parameter { Name = "TaskId", DataType = DataType.String },
                            },
                            ReturnType = DataType.String
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The method 'GetCapabilities' failed.");
                return null;
            }
        }

        public override async Task ExecuteFunction(IAsyncStreamReader<BundledRows> requestStream,
                                                   IServerStreamWriter<BundledRows> responseStream,
                                                   ServerCallContext context)
        {
            var response = new QlikResponse();

            try
            {
                logger.Debug("The method 'ExecuteFunction' is called...");

                //Read function header
                var functionHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<FunctionRequestHeader>();

                //Read common header
                var commonHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<CommonRequestHeader>();

                //Set appid
                logger.Info($"The Qlik app id '{commonHeader?.AppId}' in header found.");
                var qlikAppId = commonHeader?.AppId;

                //Set qlik user
                logger.Info($"The Qlik user '{commonHeader?.UserId}' in header found.");
                var domainUser = new DomainUser(commonHeader?.UserId);

                //Very important code line
                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                //Read parameter from qlik
                var row = GetParameter(requestStream);
                var userJson = GetParameterValue(0, row);

                //Parse request from qlik script
                logger.Debug("Parse user request...");
                var request = QlikRequest.Parse(domainUser, qlikAppId, userJson);

                var functionCall = (ConnectorFunction)functionHeader.FunctionId;
                logger.Debug($"Call Function id: '{functionCall}' from client '{context?.Peer}'.");

                if (functionCall == ConnectorFunction.START)
                {
                    #region Switch qlik user to app owner
                    if (domainUser?.UserId == "sa_scheduler" && domainUser?.UserDirectory == "INTERNAL")
                    {
                        try
                        {
                            var oldUser = domainUser.ToString();
                            domainUser = new DomainUser("INTERNAL\\ser_scheduler");
                            logger.Debug($"Change Qlik user '{oldUser}' to task service user '{domainUser}'.");
                            var connection = RuntimeOptions.Config.Connection;
                            var tmpsession = RuntimeOptions.SessionHelper.Manager.CreateNewSession(connection, domainUser, qlikAppId);
                            if (tmpsession == null)
                                throw new Exception("No session cookie generated. (Qlik Task)");
                            var qrsHub = new QlikQrsHub(RuntimeOptions.Config.Connection.ServerUri, tmpsession.Cookie);
                            qrsHub.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;
                            domainUser = request.GetAppOwner(qrsHub, qlikAppId);
                            if (domainUser == null)
                                throw new Exception("The owner of the App could not found.");
                            logger.Debug($"App owner '{domainUser}' found.");
                            request.QlikUser = domainUser;
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Could not switch the task user to real qlik user.");
                        }
                    }
                    #endregion

                    #region Function call SER.START
                    logger.Debug("Function call SER.START...");
                    var newManagedTask = new ManagedTask()
                    {
                        StartTime = DateTime.Now,
                        Message = "Create new report job...",
                        Cancellation = new CancellationTokenSource(),
                        Status = 0
                    };
                    RuntimeOptions.TaskPool.ManagedTasks.TryAdd(newManagedTask.Id, newManagedTask);
                    var startFunction = new StartFunction(RuntimeOptions);
                    startFunction.StartReportJob(request, newManagedTask);
                    response.TaskId = newManagedTask.Id.ToString();
                    #endregion
                }
                else if (functionCall == ConnectorFunction.STOP)
                {
                    #region Function call SER.STOP
                    logger.Debug("Function call SER.STOP...");
                    var stopFunction = new StopFunction(RuntimeOptions);
                    stopFunction.StopReportJobs(request);
                    if (request.ManagedTaskId == "all")
                        response.Log = "All report jobs is stopping...";
                    else
                        response.Log = $"Report job '{request.ManagedTaskId}' is stopping...";
                    response.Status = 4;
                    #endregion
                }
                else if (functionCall == ConnectorFunction.RESULT)
                {
                    #region Function call SER.RESULT
                    logger.Debug("Function call SER.RESULT...");
                    var resultFunction = new ResultFunction(RuntimeOptions);
                    response = resultFunction.FormatJobResult(request);
                    #endregion
                }
                else if (functionCall == ConnectorFunction.STATUS)
                {
                    #region Function call SER.STATUS
                    logger.Debug("Function call SER.STATUS...");
                    var statusFunction = new StatusFunction(RuntimeOptions);
                    response = statusFunction.GetStatusResponse(request);
                    #endregion
                }
                else
                {
                    throw new Exception($"The id '{functionCall}' of the function call was unknown.");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"The method 'ExecuteFunction' failed with error '{ex.Message}'.");
                response.Status = -1;
                response.SetErrorMessage(ex);
            }
            finally
            {
                logger.Trace($"Qlik status result: {JsonConvert.SerializeObject(response)}");
                await responseStream.WriteAsync(GetResult(response));
                LogManager.Flush();
            }
        }
        #endregion
    }
}