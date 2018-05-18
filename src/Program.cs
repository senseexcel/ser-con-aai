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
    using Microsoft.Extensions.PlatformAbstractions;
    using NLog;
    using NLog.Config;
    using PeterKottas.DotNetCore.WindowsService;
    using Q2g.HelperPem;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    #endregion

    class Program
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        static void Main(string[] args)
        {
            try
            {
                SetLoggerSettings("App.config");
                ServiceRunner<SSEtoSER>.Run(config =>
                {
                    config.SetDisplayName("Qlik Connector for SER");
                    config.SetDescription("Sense Excel Reporting Connector Service");
                    var name = config.GetDefaultName();
                    config.Service(serviceConfig =>
                    {
                        serviceConfig.ServiceFactory((extraArguments, controller) =>
                        {
                            return new SSEtoSER();
                        });
                        serviceConfig.OnStart((service, extraParams) =>
                        {
                            logger.Debug($"Service {name} started");
                            service.Start();
                        });
                        serviceConfig.OnStop(service =>
                        {
                            logger.Debug($"Service {name} stopped");
                            service.Stop();
                        });
                        serviceConfig.OnError(ex =>
                        {
                            logger.Error($"Service Exception: {ex}");
                        });
                    });
                });

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                Environment.Exit(1);
            }
        }

        #region Private Methods
        private static void SetLoggerSettings(string configName)
        {
            var path = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, configName);
            if (!File.Exists(path))
            {
                var root = new FileInfo(path).Directory?.Parent?.Parent?.Parent;
                var files = root.GetFiles("App.config", SearchOption.AllDirectories).ToList();
                path = files.FirstOrDefault()?.FullName;
            }

            logger.Factory.Configuration = new XmlLoggingConfiguration(path, false);
        }

        //NUR ZUM DEBUGGEN DES INSTALL-PROZESSES
        private static void InstallTest()
        {
            var manager = new TaskManager();
            var certPath = @"C:\Users\MBerthold\AppData\Roaming\senseexcel\reporting\serconnector.pem";
            var task = manager.GetSession(new Api.SerConnection()
            {
                ServerUri = new Uri("http://nb-fc-208000/ser"),
                App = "dfacdb29-6cee-4cc6-b8b1-7a89014394dd",
                Credentials = new Api.SerCredentials()
                {
                    Cert = certPath,
                    PrivateKey = @"C:\Users\MBerthold\AppData\Roaming\senseexcel\reporting\serconnector_private.key",
                    Key = "X-Qlik-Session-ser",
                }
            }, new UserParameter() { AppId = "dfacdb29-6cee-4cc6-b8b1-7a89014394dd",
               DomainUser = new Api.DomainUser("nb-fc-208000\\mberthold") });

            var test = manager.GetAllTaskForAppId("dfacdb29-6cee-4cc6-b8b1-7a89014394dd");

            var installer = new AutoInstaller(task.ConnectUri, task.Cookie);
            installer.Run(new InstallParameter()
            {
                Prefix = "demo",
                CookieHeaderName = "X-Qlik-Session-demo",
                CertificatePath = certPath,
                ExtentionPath = @"C:\Users\MBerthold\Downloads\ser-ext-ondemand.zip"
            });
        }
        #endregion
    }
}