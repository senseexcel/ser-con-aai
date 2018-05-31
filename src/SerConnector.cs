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
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using NLog;
    using Qlik.Sse;
    using System;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using Hjson;
    using Ser.Api;
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using PeterKottas.DotNetCore.WindowsService.Interfaces;
    using PeterKottas.DotNetCore.WindowsService.Base;
    using Q2g.HelperPem;
    using System.Net;
    using Q2g.HelperQrs;
    using System.Threading.Tasks;
    using System.Threading;
    #endregion

    public class SSEtoSER : MicroService, IMicroService
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        private Server server;
        private SerEvaluator serEvaluator;
        private delegate IPHostEntry GetHostEntryHandler(string name);
        private static SerOnDemandConfig config;
        #endregion

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

        private bool CheckConnection()
        {
            try
            {
                var connection = config.Connection;
                var task = new ActiveTask()
                {
                    AppId = connection.App,
                    UserId = new DomainUser("INTERNAL\\ser_scheduler"),
                };

                var taskManager = new TaskManager();
                var session = taskManager.GetSession(connection, task);
                if (session.Cookie != null)
                {
                    logger.Debug("The connection to Qlik Sense was successful.");
                    return true;
                }

                logger.Error("NO PROXY CONNECTION TO QLIK SENSE!!!");
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Connection check failed.");
                return false;
            }
        }
        #endregion

        #region Public Methods
        public static void CheckQlikConnection()
        {
            var newTask = Task<bool>.Factory.StartNew(() =>
            {
                try
                {
                    var connection = config.Connection;
                    var task = new ActiveTask()
                    {
                        AppId = connection.App,
                        UserId = new DomainUser("INTERNAL\\ser_scheduler"),
                    };

                    var taskManager = new TaskManager();
                    var session = taskManager.GetSession(connection, task);
                    if (session?.Cookie != null)
                    {
                        logger.Info("The connection to Qlik Sense was successful.");
                        return true;
                    }

                    logger.Error("NO PROXY CONNECTION TO QLIK SENSE!!!");
                    return false;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Connection check failed.");
                    return false;
                }
            }).ContinueWith((preTask) =>
            {
                if (!preTask.Result)
                {
                    Thread.Sleep(30000);
                    CheckQlikConnection();
                }
            });
        }

        public void Start()
        {
            try
            {
                var configPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "config.hjson");
                if (!File.Exists(configPath))
                {
                    var exampleConfigPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "config.hjson.example");
                    if (File.Exists(exampleConfigPath))
                        File.Copy(exampleConfigPath, configPath);
                    else
                        throw new Exception($"config file {configPath} not found.");
                }
                var json = HjsonValue.Load(configPath).ToString();
                var configObject = JObject.Parse(json);

                //Gernerate virtual config for default values
                var fullQualifiedHostname = ServerUtils.GetFullQualifiedHostname(2000);
                var vconnection = new SerOnDemandConfig()
                {
                    Connection = new SerConnection()
                    {
                        ServerUri = new Uri($"https://{fullQualifiedHostname}/ser")
                    }
                };
                var virtConnection = JObject.Parse(JsonConvert.SerializeObject(vconnection, Formatting.Indented));
                virtConnection.Merge(configObject);
                config = JsonConvert.DeserializeObject<SerOnDemandConfig>(virtConnection.ToString());
                logger.Debug($"ServerUri: {config.Connection.ServerUri}");

                //Read Assembly versions
                var enginePath = PathUtils.GetFullPathFromApp(config.SerEnginePath);
                config.PackageVersions = VersionUtils.ReadAssemblyVersions(enginePath);
                foreach (var package in config.PackageVersions)
                    logger.Debug($"Assembly: {package.Name} / {package.Version}");

                //check to generate certifiate and private key if not exists
                var certFile = config?.Connection?.Credentials?.Cert ?? null;
                certFile = PathUtils.GetFullPathFromApp(certFile);
                if (!File.Exists(certFile))
                {
                    var privateKeyFile = config?.Connection?.Credentials?.PrivateKey ?? null;
                    privateKeyFile = PathUtils.GetFullPathFromApp(privateKeyFile);
                    if (File.Exists(privateKeyFile))
                        privateKeyFile = null;

                    CreateCertificate(certFile, privateKeyFile);
                }

                logger.Debug($"Plattfom: {config.OS}");
                logger.Debug($"Architecture: {config.Architecture}");
                logger.Debug($"Framework: {config.Framework}");
                logger.Debug("Service running...");
                logger.Debug($"Start Service on Port \"{config.BindingPort}\" with Host \"{config.BindingHost}");
                logger.Debug($"Server start...");

                logger.Debug($"Check qlik connection...");
                //CheckQlikConnection();

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
            catch (Exception ex)
            {
                logger.Error(ex, "Service could not be started.");
            }
        }

        public void Stop()
        {
            try
            {
                logger.Info("Shutdown SSEtoSER...");
                server?.ShutdownAsync().Wait();
                serEvaluator.Dispose();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Service could not be stoppt.");
            }
        }
        #endregion
    }
}