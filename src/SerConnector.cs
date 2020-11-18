namespace Ser.ConAai
{
    #region Usings
    using Grpc.Core;
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using NLog;
    using Qlik.Sse;
    using System;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using Hjson;
    using Ser.Api;
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using PeterKottas.DotNetCore.WindowsService.Interfaces;
    using PeterKottas.DotNetCore.WindowsService.Base;
    using Q2g.HelperPem;
    using System.Net;
    using System.Threading.Tasks;
    using System.Threading;
    using Q2g.HelperQlik;
    #endregion

    public class SSEtoSER : MicroService, IMicroService
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        private Server server;
        private SerEvaluator serEvaluator;
        private CancellationTokenSource cts;
        private dynamic ConfigObject = null;
        private delegate IPHostEntry GetHostEntryHandler(string name);
        #endregion

        public SSEtoSER()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += ValidationCallback.ValidateRemoteCertificate;
        }

        #region Private Methods
        private static void CreateCertificate(string certFile, string privateKeyFile)
        {
            try
            {
                var cert = new X509Certificate2();
                cert = cert.GenerateQlikJWTConformCert($"CN=SER-{Environment.MachineName}",
                                                       $"CN=SER-{Environment.MachineName}-CA");
                cert.SavePem(certFile, privateKeyFile);
                logger.Debug($"Certificate created under {certFile}.");
                logger.Debug($"Private key file created under {privateKeyFile}.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"The Method {nameof(CreateCertificate)} was failed.");
            }
        }

        private Uri QlikConnectionCheck(string configJson, string serverUrl)
        {

            try
            {
                logger.Debug($"Use Url: {serverUrl}");
                if (String.IsNullOrEmpty(serverUrl))
                    return null;
                dynamic configObject = JObject.Parse(configJson);
                var serverUri = new Uri($"{serverUrl}");
                if (!serverUrl.EndsWith("/ser"))
                    serverUri = new Uri($"{serverUrl}/ser");
                configObject.connection.serverUri = serverUri;
                ConnectorConfig connectorConfig = JsonConvert.DeserializeObject<ConnectorConfig>(configObject.ToString());

                ValidationCallback.Connection = connectorConfig.Connection;
                var qlikUser = new DomainUser("INTERNAL\\ser_scheduler");
                var taskManager = new SessionHelper();
                var session = taskManager.GetSession(connectorConfig.Connection, qlikUser, null);
                if (session?.Cookie != null)
                {
                    logger.Info("The connection to Qlik Sense was successful.");
                    return serverUri;
                }
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The connection check to qlik has an error");
                return null;
            }
        }

        private Uri CheckAlternativeUris(string configJson)
        {
            try
            {
                Uri result = null;
                logger.Info("Connecting with Qlik Sense...");

                // Check with alternative txt
                var alternativeTxtPath = Path.Combine(AppContext.BaseDirectory, "alternativdns.txt");
                logger.Debug($"Take from 'alternativdns.txt'. from {alternativeTxtPath}");
                if (File.Exists(alternativeTxtPath))
                {
                    var content = File.ReadAllText(alternativeTxtPath)?.Trim();
                    logger.Info($"Check server uri \"{content}\" with alternative dns.");
                    result = QlikConnectionCheck(configJson, content);
                    if (result != null)
                        return result;
                }

                logger.Debug("Take from config.hjson.");
                result = QlikConnectionCheck(configJson, ConfigObject?.connection?.serverUri?.ToString() ?? "");
                if (result != null)
                    return result;

                logger.Debug("Search for certificate domain.");
                var alternativeUris = ConnectionFallbackHelper.AlternativeUris ?? new List<Uri>();
                foreach (var alternativeUri in alternativeUris)
                {
                    logger.Info($"Check server uri \"{alternativeUri.AbsoluteUri}\" with certificate domain.");
                    // Check with cert dns
                    result = QlikConnectionCheck(configJson, alternativeUri.AbsoluteUri.Trim('/'));
                    if (result != null)
                        return result;
                }

                // Check with hostname of the server
                logger.Debug("Take from Machine name.");
                var fullQualifiedHostname = HelperUtilities.GetFullQualifiedHostname(2000);
                logger.Info($"Check server uri with hostname \"{fullQualifiedHostname}\".");
                result = QlikConnectionCheck(configJson, $"https://{fullQualifiedHostname}");
                if (result != null)
                    return result;

                logger.Error("NO CONNECTION TO QLIK SENSE!!!");
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Connection check failed.");
                return null;
            }
        }

        private Task CheckQlikConnection(string json)
        {
            return Task.Run(() =>
            {
                var serverUri = CheckAlternativeUris(json);
                while (serverUri == null)
                {
                    logger.Error("There is no connection to Qlik.");
                    logger.Error("Please edit the right url in the connector config and check the qlik services.");
                    logger.Error("The connection to qlik is checked every 20 seconds.");
                    Thread.Sleep(20000);
                    serverUri = CheckAlternativeUris(json);
                }
                StartupConnector(serverUri);
            }, cts.Token);
        }

        private Task StartRestServer(string[] arguments)
        {
            return Task.Run(() =>
            {
                Ser.Engine.Rest.Program.Main(arguments);
            }, cts.Token);
        }

        private void StartupConnector(Uri serverUri)
        {
            // Find the right server uri
            var serverUriObj = ConfigObject?.connection?.serverUri;
            if (serverUriObj == null)
            {
                logger.Info($">>> Write the correct server uri '{serverUri?.AbsoluteUri?.Trim('/')}' in the config file - run in alternative mode. <<<");
                ConfigObject.connection.serverUri = serverUri;
            }

            var config = JsonConvert.DeserializeObject<ConnectorConfig>(ConfigObject.ToString());
            if (config.StopTimeout < 5)
                config.StopTimeout = 5;

            //Start Rest Service
            var rootContentFolder = HelperUtilities.GetFullPathFromApp(config.WorkingDir);
            var arguments = new List<string>() { $"--Urls={config.RestServiceUrl}", $"--contentRoot={rootContentFolder}" };
            StartRestServer(arguments.ToArray());
            config.PackageVersion = VersionUtils.GetMainVersion();
            logger.Info($"MainVersion: {config.PackageVersion}");
            config.ExternalPackageJson = VersionUtils.GetExternalPackageJson();
            var packages = JArray.Parse(config.ExternalPackageJson);
            foreach (var package in packages)
                logger.Info($"Package: {JsonConvert.SerializeObject(package)}");

            logger.Debug($"Plattfom: {config.OS}");
            logger.Debug($"Architecture: {config.Architecture}");
            logger.Debug($"Framework: {config.Framework}");
            logger.Debug("Service running...");
            logger.Debug($"Start Service on Port \"{config.BindingPort}\" with Host \"{config.BindingHost}");
            logger.Debug($"Server start...");

            using (serEvaluator = new SerEvaluator(config))
            {
                server = new Server()
                {
                    Services = { Connector.BindService(serEvaluator) },
                    Ports = { new ServerPort(config.BindingHost, config.BindingPort, ServerCredentials.Insecure) },
                };

                server.Start();
                logger.Info($"gRPC listening on port {config.BindingPort} on Host {config.BindingHost}");
                logger.Info($"Ready...");
            }
        }
        #endregion

        #region Public Methods
        public void Start()
        {
            try
            {
                cts = new CancellationTokenSource();
                var configPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "config.hjson");
                if (!File.Exists(configPath))
                {
                    var exampleConfigPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "config.hjson.example");
                    if (File.Exists(exampleConfigPath))
                        File.Copy(exampleConfigPath, configPath);
                    else
                        throw new Exception($"config file {configPath} not found.");
                }

                logger.Debug($"Read conncetor config file from '{configPath}'.");
                var json = HjsonValue.Load(configPath).ToString();
                ConfigObject = JObject.Parse(json);

                // Check to generate certifiate and private key if not exists
                var certFile = ConfigObject?.connection?.credentials?.cert?.ToString() ?? null;
                certFile = HelperUtilities.GetFullPathFromApp(certFile);
                if (!File.Exists(certFile))
                {
                    var privateKeyFile = ConfigObject?.connection?.credentials?.privateKey.ToString() ?? null;
                    privateKeyFile = HelperUtilities.GetFullPathFromApp(privateKeyFile);
                    if (File.Exists(privateKeyFile))
                        privateKeyFile = null;

                    CreateCertificate(certFile, privateKeyFile);
                }

                // Wait for connection to Qlik
                CheckQlikConnection(json);
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "Service could not be started.");
            }
        }

        public void Stop()
        {
            try
            {
                logger.Info("Shutdown SSEtoSER...");
                cts?.Cancel();
                server?.ShutdownAsync().Wait();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Service could not be stoppt.");
            }
        }
        #endregion
    }
}