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
    using Ser.Distribute;
    using Qlik.EngineAPI;
    using enigma;
    using ImpromptuInterface;
    using System.Net.WebSockets;
    using System.Threading;
    #endregion

    public class TaskManager
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Variables & Properties
        private List<ActiveTask> Tasks = new List<ActiveTask>();
        private List<SessionInfo> Sessions = new List<SessionInfo>();
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

        public IDoc GetSessionAppConnection(Uri uri, Cookie cookie, string appId)
        {
            try
            {
                var url = ServerUtils.MakeWebSocketFromHttp(uri);
                var connId = Guid.NewGuid().ToString();
                url = $"{url}/app/engineData/identity/{connId}";
                var config = new EnigmaConfigurations()
                {
                    Url = url,
                    CreateSocket = async (Url) =>
                    {
                        var webSocket = new ClientWebSocket();
                        webSocket.Options.RemoteCertificateValidationCallback = ValidationCallback.ValidateRemoteCertificate;
                        webSocket.Options.Cookies = new CookieContainer();
                        cookie.HttpOnly = false;
                        webSocket.Options.Cookies.Add(cookie);
                        await webSocket.ConnectAsync(new Uri(Url), CancellationToken.None);
                        return webSocket;
                    },
                };
                var session = Enigma.Create(config);
                var globalTask = session.OpenAsync();
                globalTask.Wait();
                IGlobal global = Impromptu.ActLike<IGlobal>(globalTask.Result);
                var app = global.OpenDocAsync(appId).Result;
                logger.Debug("websocket - success");
                return app;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "create websocket connection was failed.");
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
        public List<ActiveTask> GetAllTaskForAppId(string appId)
        {
            try
            {
                return Tasks.Where(t => t.AppId == appId).ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        public List<ActiveTask> GetAllTasksForUser(Uri serverUri, Cookie cookie, DomainUser user)
        {
            var qrsHub = new QlikQrsHub(serverUri, cookie);
            var filter = $"owner.userid eq '{user.UserId}'&owner.userDirectory eq '{user.UserDirectory}'";
            var results = qrsHub.SendRequestAsync("app/full", HttpMethod.Get, null, filter).Result;
            var taskList = new List<ActiveTask>();
            if (results == null)
                return taskList;

            var apps = JArray.Parse(results).ToList();
            foreach (var app in apps)
            {
                var owner = JsonConvert.DeserializeObject<Owner>(app["owner"].ToString());
                if (owner.ToString() == user.ToString())
                    taskList.AddRange(Tasks.Where(t => t.AppId == app["id"].ToString()));
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
                var result = Tasks.FirstOrDefault(t => t.Id == taskId) ?? null;
                return result;
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
                lock (this)
                {
                    var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                    Tasks.Remove(task);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        public ActiveTask CreateTask(UserParameter parameter)
        {
            try
            {
                var newTask = new ActiveTask()
                {
                    Status = 0,
                    Id = Guid.NewGuid().ToString(),
                    StartTime = DateTime.Now,
                    AppId = parameter.AppId,
                    UserId = parameter.DomainUser,
                };

                lock (this)
                {
                    Tasks.Add(newTask);
                }
                return newTask;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        public SessionInfo GetSession(SerConnection connection, ActiveTask task)
        {
            try
            {
                var domainUser = task.UserId;
                lock (this)
                {
                    var uri = connection.ServerUri;
                    var oldTask = GetRunningTask(task.Id);
                    var oldSession = Sessions?.FirstOrDefault(u => u.ConnectUri.OriginalString == uri.OriginalString
                                                          && u.UserId.ToString() == domainUser.ToString()
                                                          && u.AppId == task.AppId) ?? null;
                    if (oldSession != null)
                    {
                        var result = ValidateSession(oldSession.ConnectUri, oldSession.Cookie);
                        if (result)
                        {
                            task.Session = oldSession;
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
                        AppId = task.AppId,
                        UserId = task.UserId,
                    };
                    Sessions.Add(sessionInfo);
                    task.Session = sessionInfo;
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