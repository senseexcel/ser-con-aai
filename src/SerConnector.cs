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
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using NLog;
    using Qlik.Sse;
    using System;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using PemCrypto;
    #endregion

    public class SSEtoSER
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        private Server server;
        private SerEvaluator serEvaluator;
        #endregion

        #region Private Methods
        private static X509Certificate2 CreateCertificate(string savePath)
        {
            var cert = new X509Certificate2();
            cert = cert.GenerateQlikJWTConformCert($"CN={Environment.MachineName}",
                                                   $"CN={Environment.MachineName}-CA");
            cert.SavePem(savePath, true);
            return cert;
        }
        #endregion

        #region Public Methods
        public void Start(string[] args)
        {
            try
            {
                var configPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "config.json");
                if (!File.Exists(configPath))
                    throw new Exception($"config file {configPath} not found.");
                var json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<SerOnDemandConfig>(json);

                logger.Info(config.Fullname);
                logger.Info($"Plattfom: {config.OS}");
                logger.Info($"Architecture: {config.Architecture}");
                logger.Info($"Framework: {config.Framework}");

                var mode = args?.FirstOrDefault() ?? null;
                if (mode != null)
                {
                    if (mode == "-cert")
                    {
                        Console.WriteLine($"Certificate generate into file {config.CertPath}");
                        CreateCertificate(config.CertPath);
                        Console.WriteLine("Please press any key to terminate.");
                        return;
                    }
                }

                Console.WriteLine("Service running...");
                Console.WriteLine($"Start Service on Port \"{config.Port}\" with Host \"{config.Host}\"...");
                logger.Info($"Server start...");

                using (serEvaluator = new SerEvaluator(config))
                {
                    server = new Server()
                    {
                        Services = { Connector.BindService(serEvaluator) },
                        Ports = { new ServerPort(config.Host, config.Port, ServerCredentials.Insecure) },
                    };

                    server.Start();
                    logger.Info($"gRPC listening on port {config.Port} on Host {config.Host}");
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