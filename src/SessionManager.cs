﻿#region License
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
    #endregion

    public class SessionManager
    {
        class SessionInfo
        {
            public Cookie Cookie { get; set; }
            public DomainUser User { get; set; }
            public Uri ConnectUri { get; set; }
        }

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
                connectUri = new Uri($"{connectUri.OriginalString}/sense/app");
                var cookieContainer = new CookieContainer();
                var connectionHandler = new HttpClientHandler
                {
                    UseDefaultCredentials = true,
                    CookieContainer = cookieContainer
                };

                var connection = new HttpClient(connectionHandler);
                connection.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                connection.GetAsync(connectUri).Wait();
                var responseCookies = cookieContainer?.GetCookies(connectUri)?.Cast<Cookie>() ?? null;
                var cookie = responseCookies.FirstOrDefault(c => c.Name.Equals(cookieName)) ?? null;
                logger.Debug($"The session cookie was found. {cookie.Name} - {cookie.Value}");
                return cookie;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Can´t create session cookie with JWT.");
                return null;
            }
        }

        private bool ValidateSession(Uri connectUri)
        {
            try
            {
                var hubUri = new Uri($"{connectUri.OriginalString}/hub");
                var connection = new HttpClient();
                connection.GetAsync(hubUri).Wait();
                return true;
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region Public Methods
        public Cookie GetSession(Uri connectUri, DomainUser domainUser, string cookieName, string virtualProxy, string certName)
        {
            try
            {
                var cert = new X509Certificate2();
                var fullUri = new Uri($"{connectUri.OriginalString}/{virtualProxy}");
                lock (this)
                {
                    var oldSession = sessionList?.FirstOrDefault(u => u.ConnectUri.OriginalString == connectUri.OriginalString
                                                                 && u.User.Equals(domainUser)) ?? null;
                    if (oldSession != null)
                        return oldSession.Cookie;
                }
                var certPath = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, certName);
                if (!File.Exists(certPath))
                {
                    var exeName = Path.GetFileName(Assembly.GetExecutingAssembly().FullName);
                    logger.Warn($"No Certificate {certPath} exists. Please generate a Certificate with \"{exeName} -cert\"");
                }
                else
                {
                    var name = Path.GetFileNameWithoutExtension(certPath);
                    var privateFile = Directory.GetFiles(Path.GetDirectoryName(certPath), $"{name}_private.key",
                                                         SearchOption.TopDirectoryOnly).FirstOrDefault() ?? null;
                    cert = cert.LoadPem(certPath, privateFile);
                }

                var claims = new[]
                {
                    new Claim("UserDirectory",  domainUser.UserDirectory),
                    new Claim("UserId", domainUser.UserId),
                    new Claim("Attributes", "[SerOnDemand]")
                }.ToList();
                var token = cert.GenerateQlikJWToken(claims, TimeSpan.FromMinutes(20));
                logger.Debug($"Generate token {token}");
                var cookie = GetJWTSession(fullUri, token, cookieName);
                logger.Debug($"Generate cookie {cookie.Name} - {cookie.Value}");
                if (cookie != null)
                {
                    sessionList.Add(new SessionInfo()
                    {
                        Cookie = cookie,
                        User = domainUser,
                        ConnectUri = connectUri
                    });
                    return cookie;
                }

                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The session could not be created.");
                return null;
            }
        }
        #endregion
    }
}