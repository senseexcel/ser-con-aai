namespace Ser.ConAai
{
    #region Usings
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using NLog;
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ConnectorConfig
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties
        public string WorkingDir { get; set; }
        public int BindingPort { get; set; } = 50059;
        public string BindingHost { get; set; } = "localhost";
        public string RestServiceUrl { get; set; } = "http://localhost:40263";
        public int CleanupTimeout { get; set; } = 20000;
        public SerConnection Connection { get; set; }

        public string Framework { get; private set; } = RuntimeInformation.FrameworkDescription;
        public string OS { get; private set; } = RuntimeInformation.OSDescription;
        public string Architecture { get; private set; } = RuntimeInformation.OSArchitecture.ToString();
        public string AppVersion { get; private set; } = PlatformServices.Default.Application.ApplicationVersion;
        public string AppName { get; private set; } = PlatformServices.Default.Application.ApplicationName;
        public string PackageVersion { get; set; }
        public string ExternalPackageJson { get; set; }
        #endregion

        public string GetCertPath()
        {
            try
            {
                if (String.IsNullOrEmpty(Connection?.Credentials?.Cert))
                    return null;

                if (File.Exists(Connection?.Credentials?.Cert))
                    return Connection.Credentials.Cert;

                return Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, Connection?.Credentials?.Cert);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }
    }
}
