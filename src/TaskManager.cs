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
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using Microsoft.Extensions.PlatformAbstractions;
    using System.Linq;
    using Q2g.HelperPem;
    using System.Net;
    using System.Net.Http;
    using NLog;
    using System.Reflection;
    using Ser.Api;
    using System.Security.Claims;
    using Newtonsoft.Json;
    using Q2g.HelperQrs;
    using Newtonsoft.Json.Linq;
    #endregion

    public class SessionInfo
    {
        #region Properties
        public Cookie Cookie { get; set; }
        public Uri ConnectUri { get; set; }
        public string AppId { get; set; }
        public string UserId { get; set; }
        public List<ActiveTask> ActiveTasks { get; set; } = new List<ActiveTask>();
        #endregion
    }

    public class TaskManager
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Variables & Properties
        public List<SessionInfo> Sessions { get; private set; } = new List<SessionInfo>();
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

        private bool ValidateSession(Uri serverUri, Cookie cookie)
        {
            try
            {
                var qrsHub = new QlikQrsHub(serverUri, cookie);
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
        #endregion

        #region Public Methods
        public List<SessionInfo> GetAllTaskForAppId(string appId)
        {
            return Sessions.Where(l => l.AppId == appId).ToList();
        }

        public List<SessionInfo> GetAllTasksForUser(Uri serverUri, Cookie cookie, DomainUser user)
        {
            var qrsHub = new QlikQrsHub(serverUri, cookie);
            var results = qrsHub.SendRequestAsync("app/full", HttpMethod.Get).Result;
            if (results == null)
                return new List<SessionInfo>();

            var apps = JArray.Parse(results).ToList();
            var taskList = new List<SessionInfo>();
            foreach (var task in Sessions)
            {
                foreach (var app in apps)
                {
                    var owner = JsonConvert.DeserializeObject<Owner>(app["owner"].ToString());
                    if (owner.ToString() == user.ToString())
                    {
                        //task.AppName = app["name"].ToString() ?? null;
                        taskList.Add(task);
                    }
                }
            }

            return taskList;
        }

        public string GetToken(DomainUser domainUser, SerConnection connection, TimeSpan untilValid)
        {
            try
            {
                var cert = new X509Certificate2();
                var certPath = PathUtils.GetFullPathFromApp(connection.Credentials.Cert);
                logger.Debug($"CERTPATH: {certPath}");
                var privateKey = PathUtils.GetFullPathFromApp(connection.Credentials.PrivateKey);
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
                logger.Error(ex);
                return null;
            }
        }

        public ActiveTask GetRunningTask(string taskId)
        {
            try
            {
                foreach (var session in Sessions)
                {
                    var task = session?.ActiveTasks?.FirstOrDefault(t => t.TaskId == taskId) ?? null;
                    if (task != null)
                        return task;
                }

                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        public void RemoveTask(string taskId)
        {
            try
            {
                foreach (var session in Sessions)
                {
                    var task = session?.ActiveTasks?.FirstOrDefault(t => t.TaskId == taskId) ?? null;
                    if (task != null)
                        session.ActiveTasks.Remove(task);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        public SessionInfo GetSession(SerConnection connection, UserParameter parameter, ActiveTask newTask = null)
        {
            try
            {
                var domainUser = parameter.DomainUser;
                lock (this)
                {
                    var uri = connection.ServerUri;
                    var oldSession = Sessions?.FirstOrDefault(u => u.ConnectUri.OriginalString == uri.OriginalString
                                                          && u.UserId == parameter.DomainUser.ToString()
                                                          && u.AppId == parameter.AppId) ?? null;
                    if (oldSession != null)
                    {
                        var result = ValidateSession(oldSession.ConnectUri, oldSession.Cookie);
                        if (result)
                        {
                            if (newTask != null)
                                oldSession.ActiveTasks.Add(newTask);
                            return oldSession;
                        }
                        Sessions.Remove(oldSession);
                    }
                }

                var token = GetToken(domainUser, connection, TimeSpan.FromMinutes(20));
                logger.Debug($"Generate token {token}");
                var cookie = GetJWTSession(connection.ServerUri, token, connection.Credentials.Key);
                logger.Debug($"Generate cookie {cookie?.Name} - {cookie?.Value}");
                if (cookie != null)
                {
                    var sessionInfo = new SessionInfo()
                    {
                        Cookie = cookie,
                        ConnectUri = connection.ServerUri,
                        AppId = parameter.AppId,
                        UserId = parameter.DomainUser.ToString(),
                    };
                    if (newTask != null)
                        sessionInfo.ActiveTasks.Add(newTask);
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
        #endregion
    }
}