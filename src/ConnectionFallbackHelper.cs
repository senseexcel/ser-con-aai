namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using NLog;
    #endregion

    public class ConnectionFallbackHelper
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties && Variables
        public static List<Uri> AlternativeUris { get; private set; }
        #endregion

        public static void CertificateFallbackValidation(object sender, X509Certificate2 cert)
        {
            try
            {
                if (AlternativeUris != null)
                    return;

                AlternativeUris = new List<Uri>();
                var dnsNames = new List<string>();
                dnsNames.Add(cert.Subject.Split(',').FirstOrDefault().Replace("CN=", ""));
                var bytehosts = cert?.Extensions["2.5.29.17"] ?? null;
                if (bytehosts != null)
                {
                    var names = bytehosts.Format(false).Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var name in names)
                        dnsNames.Add(name.Replace("DNS-Name=", "").Trim());
                }
                if (sender is Uri serverUri)
                {
                    foreach (var dnsName in dnsNames)
                    {
                        var uriBuilder = new UriBuilder(serverUri)
                        {
                            Host = dnsName
                        };
                        AlternativeUris.Add(uriBuilder.Uri);
                    }
                    AlternativeUris = AlternativeUris?.Distinct()?.ToList() ?? new List<Uri>();
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The alternative dns names could´t not read.");
            }
        }
    }
}
