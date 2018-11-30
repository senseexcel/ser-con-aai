﻿namespace Ser.ConAai
{
    #region Usings
    using NLog;
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    #endregion

    public static class ValidationCallback
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties
        public static SerConnection Connection { get; set; }
        #endregion

        #region Public Methods
        public static bool ValidateRemoteCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors error)
        {
            try
            {
                if (error == SslPolicyErrors.None)
                    return true;

                if (!Connection.SslVerify)
                    return true;

                logger.Debug("Validate Server Certificate...");
                Uri requestUri = null;
                if (sender is HttpRequestMessage hrm)
                    requestUri = hrm.RequestUri;
                if (sender is HttpClient hc)
                    requestUri = hc.BaseAddress;
                if (sender is HttpWebRequest hwr)
                    requestUri = hwr.Address;

                if (requestUri != null)
                {
                    logger.Debug("Validate Thumbprints...");
                    var thumbprints = Connection?.SslValidThumbprints ?? new List<SerThumbprint>();
                    foreach (var item in thumbprints)
                    {
                        try
                        {
                            Uri uri = null;
                            if (!String.IsNullOrEmpty(item.Url))
                                uri = new Uri(item.Url);

                            var thumbprint = item.Thumbprint.Replace(":", "").Replace(" ", "").ToLowerInvariant();
                            var certThumbprint = cert.GetCertHashString().ToLowerInvariant();
                            if (thumbprint == certThumbprint)
                            {
                                // Use normal host
                                if ((uri == null) || (uri.Host.ToLowerInvariant() == requestUri.Host.ToLowerInvariant()))
                                    return true;
                                else
                                {
                                    // Use fallback host from certifiacte
                                    var certHost = cert.Subject.Remove(0, 3);
                                    if (certHost.ToLowerInvariant() == requestUri.Host.ToLowerInvariant())
                                        return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Thumbprint could not be validated.");
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The SSL-Validation was faild.");
                return false;
            }
        }
        #endregion
    }
}