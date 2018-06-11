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
    using Newtonsoft.Json;
    using NLog;
    using NLog.Config;
    using PeterKottas.DotNetCore.WindowsService;
    using Q2g.HelperPem;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Xml;
    using System.Xml.Linq;
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
                SetLoggerSettings();
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
        private static XmlReader GetXmlReader(string path)
        {
            var jsonContent = File.ReadAllText(path);
            var xdoc = JsonConvert.DeserializeXNode(jsonContent);
            return xdoc.CreateReader();
        }

        private static void SetLoggerSettings()
        {
            var path = String.Empty;

            try
            {
                var appPath = PlatformServices.Default.Application.ApplicationBasePath;
                var files = Directory.GetFiles(appPath, "*.*", SearchOption.TopDirectoryOnly)
                                     .Where(f => f.ToLowerInvariant().EndsWith("\\app.config") || 
                                                 f.ToLowerInvariant().EndsWith("\\app.json")).ToList();
                if (files != null && files.Count > 0)
                {
                    if (files.Count > 1)
                        throw new Exception("Too many logger configs found.");

                    path = files.FirstOrDefault();
                    var extention = Path.GetExtension(path);
                    switch (extention)
                    {
                        case ".json":
                            logger.Factory.Configuration = new XmlLoggingConfiguration(GetXmlReader(path), Path.GetFileName(path));
                            break;
                        case ".config":
                            logger.Factory.Configuration = new XmlLoggingConfiguration(path);
                            break;
                        default:
                            throw new Exception($"unkown log format {extention}.");
                    }
                }
                else
                {
                    throw new Exception("No logger config loaded.");
                }
            }
            catch
            {
                Console.WriteLine($"The logger setting are invalid!!!\nPlease check the {path} in the app folder.");
            }
        }

        //NUR ZUM DEBUGGEN DES INSTALL-PROZESSES
        private static void InstallTest(string certPath, string privateKey)
        {
            var manager = new TaskManager();
            var domainUser = new Ser.Api.DomainUser("nb-fc-208000\\mberthold");
            var task = manager.GetSession(new Api.SerConnection()
            {
                ServerUri = new Uri("http://nb-fc-208000/ser"),
                App = "dfacdb29-6cee-4cc6-b8b1-7a89014394dd",
                Credentials = new Api.SerCredentials()
                {
                    Cert = certPath,
                    PrivateKey = privateKey,
                    Key = "X-Qlik-Session-ser",
                }
            }, new ActiveTask() { AppId = "dfacdb29-6cee-4cc6-b8b1-7a89014394dd", UserId = domainUser});

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