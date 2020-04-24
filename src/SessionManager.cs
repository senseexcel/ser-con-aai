﻿namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Security.Claims;
    using System.Security.Cryptography.X509Certificates;
    using NLog;
    using Q2g.HelperPem;
    using Q2g.HelperQlik;
    using Q2g.HelperQrs;
    using Ser.Api;
    #endregion

    public class SessionManager
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Variables & Properties
        private List<SessionInfo> Sessions = new List<SessionInfo>();
        private readonly object threadObject = new object();
        #endregion

        #region Private Methods
        private bool ValidateSession(SessionInfo sessionInfo)
        {
            try
            {
                var qrsHub = new QlikQrsHub(sessionInfo.ConnectUri, sessionInfo.Cookie);
                var result = qrsHub.SendRequestAsync("about", HttpMethod.Get).Result;
                if (String.IsNullOrEmpty(result))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private Cookie GetJWTSession(Uri connectUri, string token, string cookieName = "X-Qlik-Session")
        {
            try
            {
                var newUri = new UriBuilder(connectUri);
                newUri.Path += "/sense/app";
                logger.Debug($"ConnectUri: {connectUri}");
                var fullConnectUri = newUri.Uri;
                logger.Debug($"Full ConnectUri: {fullConnectUri}");
                var cookieContainer = new CookieContainer();
                var connectionHandler = new HttpClientHandler
                {
                    UseDefaultCredentials = true,
                    CookieContainer = cookieContainer,
                };

                connectionHandler.ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    var callback = ServicePointManager.ServerCertificateValidationCallback;
                    if (callback != null)
                    {
                        var result = callback(sender, certificate, chain, sslPolicyErrors);
                        if (result)
                            return true;
                        else
                        {
                            ConnectionFallbackHelper.CertificateFallbackValidation(connectUri, certificate);
                            return false;
                        }
                    }
                    return false;
                };

                var connection = new HttpClient(connectionHandler);
                connection.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                var message = connection.GetAsync(fullConnectUri).Result;
                logger.Trace($"Message: {message}");

                var responseCookies = cookieContainer?.GetCookies(fullConnectUri)?.Cast<Cookie>() ?? null;
                var cookie = responseCookies.FirstOrDefault(c => c.Name.Equals(cookieName)) ?? null;
                logger.Debug($"The session cookie was found. {cookie?.Name} - {cookie?.Value}");
                return cookie;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Can´t create session cookie with JWT.");
                return null;
            }
        }
        #endregion

        #region Public Methods
        public string GetToken(DomainUser domainUser, SerConnection connection, TimeSpan untilValid)
        {
            try
            {
                var cert = new X509Certificate2();
                var certPath = HelperUtilities.GetFullPathFromApp(connection.Credentials.Cert);
                logger.Debug($"CERTPATH: {certPath}");
                var privateKey = HelperUtilities.GetFullPathFromApp(connection.Credentials.PrivateKey);
                logger.Debug($"PRIVATEKEY: {privateKey}");
                cert = cert.LoadPem(certPath, privateKey);
                var claims = new[]
                {
                    new Claim("UserDirectory",  domainUser.UserDirectory),
                    new Claim("UserId", domainUser.UserId),
                    new Claim("Attributes", "[SerOnDemand]")
                }.ToList();
                return cert.GenerateQlikJWToken(claims, untilValid);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Can´t create a jwt token.");
                return null;
            }
        }

        public SessionInfo GetSession(SerConnection connection, DomainUser qlikUser, string appId)
        {
            try
            {
                lock (threadObject)
                {
                    var uri = connection.ServerUri;
                    var oldSession = Sessions?.FirstOrDefault(u => u.ConnectUri.OriginalString == uri.OriginalString
                                                          && u.User.ToString() == qlikUser.ToString()
                                                          && u.AppId == appId) ?? null;
                    if (oldSession != null)
                    {
                        var result = ValidateSession(oldSession);
                        if (result)
                            return oldSession;
                        Sessions.Remove(oldSession);
                    }
                }

                var token = GetToken(qlikUser, connection, TimeSpan.FromMinutes(20));
                logger.Debug($"Generate token {token}");
                var cookie = GetJWTSession(connection.ServerUri, token, connection.Credentials.Key);
                logger.Debug($"Generate cookie {cookie?.Name} - {cookie?.Value}");
                if (cookie != null)
                {
                    var sessionInfo = new SessionInfo()
                    {
                        Cookie = cookie,
                        ConnectUri = connection.ServerUri,
                        AppId = appId,
                        User = qlikUser,
                    };
                    Sessions.Add(sessionInfo);
                    return sessionInfo;
                }

                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The session could not be created.");
                return null;
            }
        }

        public void MakeSocketFree(SessionInfo session)
        {
            if (session?.QlikConn != null)
            {
                session.QlikConn.Close();
                session.QlikConn = null;
            }
        }
        #endregion
    }

    public class SessionInfo
    {
        #region Properties
        public DomainUser User { get; set; }
        public string AppId { get; set; }
        public Cookie Cookie { get; set; }
        public Uri ConnectUri { get; set; }
        public Q2g.HelperQlik.Connection QlikConn { get; set; }
        #endregion
    }
}