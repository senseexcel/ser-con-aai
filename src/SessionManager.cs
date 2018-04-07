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
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using Microsoft.Extensions.PlatformAbstractions;
    using System.Linq;
    using Q2gHelperPem;
    using System.Net;
    using System.Net.Http;
    using NLog;
    using System.Reflection;
    using SerApi;
    using System.Security.Claims;
    using Newtonsoft.Json;
    #endregion

    public class SessionInfo
    {
        public Cookie Cookie { get; set; }
        public DomainUser User { get; set; }
        public Uri ConnectUri { get; set; }
        public int ProcessId { get; set; }
        public string DownloadLink { get; set; }
        public int Status { get; set; }
    }

    public class SessionManager
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Variables & Properties
        private List<SessionInfo> sessionList;
        #endregion

        #region Constructor
        public SessionManager()
        {
            sessionList = new List<SessionInfo>();
        }
        #endregion

        #region Private Methods
        private Cookie GetJWTSession(Uri connectUri, string token, string cookieName = "X-Qlik-Session")
        {
            try
            {
                var newUri = new UriBuilder(connectUri);
                newUri.Path +="/sense/app";
                logger.Debug($"ConnectUri: {connectUri}");
                connectUri = newUri.Uri;
                logger.Debug($"Full ConnectUri: {connectUri}");
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
                        return callback(sender, certificate, chain, sslPolicyErrors);
                    return false;
                };

                var connection = new HttpClient(connectionHandler);
                
                logger.Debug($"Bearer token: {token}");
                connection.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                var message = connection.GetAsync(connectUri).Result;
                logger.Debug($"Message: {message}");

                var responseCookies = cookieContainer?.GetCookies(connectUri)?.Cast<Cookie>() ?? null;
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
        public SessionInfo GetExistsSession(Uri connectUri, DomainUser domainUser)
        {
            var result = sessionList?.FirstOrDefault(u => u.ConnectUri.OriginalString == connectUri.OriginalString
                                                                 && u.User.UserId == domainUser.UserId 
                                                                 && u.User.UserDirectory == domainUser.UserDirectory) ?? null;
            return result;
        }

        public SessionInfo GetSession(SerConnection connection, DomainUser domainUser)
        {
            try
            {
                var cert = new X509Certificate2();               
                lock (this)
                {
                    var oldSession = GetExistsSession(connection.ServerUri, domainUser);
                    if (oldSession != null)
                        return oldSession;
                }

                var certPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, connection.Credentials.Cert);
                if (!File.Exists(certPath))
                {
                    certPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, certPath);
                    if (!File.Exists(certPath))
                    {
                        var exeName = Path.GetFileName(Assembly.GetExecutingAssembly().FullName);
                        logger.Warn($"No Certificate {certPath} exists. Please generate a Certificate with \"{exeName} -cert\"");
                    }
                }

                logger.Debug($"CERTPATH: {certPath}");
                var privateKey = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, connection.Credentials.PrivateKey);
                if (!File.Exists(privateKey))
                {
                    privateKey = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, privateKey);
                    if (!File.Exists(privateKey))
                    {
                        var exeName = Path.GetFileName(Assembly.GetExecutingAssembly().FullName);
                        logger.Warn($"No private key {privateKey} exists. Please generate a private Key with \"{exeName} -cert\"");
                    }
                }

                logger.Debug($"PRIVATEKEY: {privateKey}");
                cert = cert.LoadPem(certPath, privateKey);
                var claims = new[]
                {
                    new Claim("UserDirectory",  domainUser.UserDirectory),
                    new Claim("UserId", domainUser.UserId),
                    new Claim("Attributes", "[SerOnDemand]")
                }.ToList();
                var token = cert.GenerateQlikJWToken(claims, TimeSpan.FromMinutes(20));
                logger.Debug($"Generate token {token}");
                var cookie = GetJWTSession(connection.ServerUri, token, connection.Credentials.Key);
                logger.Debug($"Generate cookie {cookie?.Name} - {cookie?.Value}");
                if (cookie != null)
                {
                    var sessionInfo = new SessionInfo()
                    {
                        Cookie = cookie,
                        User = domainUser,
                        ConnectUri = connection.ServerUri
                    };
                    sessionList.Add(sessionInfo);
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

        public void DeleteSession(Uri connectUri, DomainUser domainUser, string taskId)
        {
            try
            {
                var session = GetExistsSession(connectUri, domainUser);
                if (session != null)
                    sessionList.Remove(session);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Session {taskId} could not deleted.");
            }
        }
        #endregion
    }
}