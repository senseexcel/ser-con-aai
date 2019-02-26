//namespace Ser.ConAai
//{
//    #region Usings
//    using enigma;
//    using ImpromptuInterface;
//    using NLog;
//    using Qlik.EngineAPI;
//    using System;
//    using System.Collections.Generic;
//    using System.Net;
//    using System.Net.Security;
//    using System.Net.WebSockets;
//    using System.Security.Cryptography.X509Certificates;
//    using System.Text;
//    using System.Threading;
//    using Ser.Connections;
//    #endregion

//    public static class QlikWebsocket
//    {
//        #region Logger
//        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
//        #endregion

//        public static SessionInfo CreateNewConnection(SessionInfo sessionInfo, bool attached = false)
//        {
//            try
//            {
//                if (String.IsNullOrEmpty(sessionInfo.AppId))
//                {
//                    logger.Debug("No appid found.");
//                    return null;
//                }
//                var url = ServerUtils.MakeWebSocketFromHttp(sessionInfo.ConnectUri);
//                var connId = Guid.NewGuid().ToString();
//                if (attached)
//                    url = $"{url}/app/{sessionInfo.AppId}";
//                else
//                    url = $"{url}/app/{sessionInfo.AppId}/identity/{connId}";
//                logger.Info($"Connect to: {url}");
//                var config = new EnigmaConfigurations()
//                {
//                    Url = url,
//                    CreateSocket = async (Url) =>
//                    {
//                        var webSocket = new ClientWebSocket();
//                        webSocket.Options.RemoteCertificateValidationCallback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
//                        {
//                            return ValidationCallback.ValidateRemoteCertificate(new Uri(url), certificate, chain, sslPolicyErrors);
//                        };
//                        webSocket.Options.Cookies = new CookieContainer();
//                        var cookie = sessionInfo.Cookie;
//                        cookie.HttpOnly = false;
//                        webSocket.Options.Cookies.Add(cookie);
//                        webSocket.Options.KeepAliveInterval = TimeSpan.FromDays(48);
//                        await webSocket.ConnectAsync(new Uri(Url), CancellationToken.None);
//                        return webSocket;
//                    },
//                };
//                var session = Enigma.Create(config);
//                var globalTask = session.OpenAsync();
//                globalTask.Wait();
//                IGlobal global = Impromptu.ActLike<IGlobal>(globalTask.Result);
//                var checkTask = global.IsDesktopModeAsync();
//                checkTask.Wait(2000);
//                if (!checkTask.IsCompletedSuccessfully)
//                    throw new Exception("No active connection.");
//                logger.Debug("Open qlik app.");
//                IDoc app = null;
//                if (attached)
//                    app =  global.GetActiveDocAsync().Result;
//                else
//                    app = global.OpenDocAsync(sessionInfo.AppId).Result;
//                sessionInfo.QlikApp = app;
//                sessionInfo.SocketSession = session;
//                return sessionInfo;
//            }
//            catch (Exception ex)
//            {
//                logger.Error(ex, "Create a websocket connection was failed.");
//                return null;
//            }
//        }
//    }
//}
