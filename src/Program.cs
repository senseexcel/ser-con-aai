namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using NLog;
    using NLog.Config;
    using PeterKottas.DotNetCore.WindowsService;
    using Ser.ConAai.Config;
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Xml;
    #endregion

    class Program
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties
        public static ConnectorService Service { get; private set; }
        #endregion

        #region Private Methods
        private static void SetLoggerSettings(string configName)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configName);
            if (!File.Exists(path))
            {
                var root = new FileInfo(path).Directory?.Parent?.Parent?.Parent;
                var files = root.GetFiles("App.config", SearchOption.AllDirectories).ToList();
                path = files.FirstOrDefault()?.FullName;
            }

            LogManager.Configuration = new XmlLoggingConfiguration(path);
        }
        #endregion

        #region Public Methods
        static void Main(string[] args)
        {
            try
            {
                SetLoggerSettings("App.config");

                if (args.Length > 0 && args[0] == "VersionNumber")
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "Version.txt"), ConnectorVersion.GetMainVersion());
                    return;
                }

                ServiceRunner<ConnectorService>.Run(config =>
                {
                    config.SetDisplayName("AnalyticsGate Connector");
                    config.SetDescription("AGR Connector Service for Qlik");
                    var name = config.GetDefaultName();
                    config.Service(serviceConfig =>
                    {
                        serviceConfig.ServiceFactory((extraArguments, controller) =>
                        {
                            Service = new ConnectorService();
                            return Service;
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
        #endregion
    }
}