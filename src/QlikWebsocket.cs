namespace Ser.ConAai
{
    #region Usings
    using enigma;
    using ImpromptuInterface;
    using NLog;
    using Qlik.EngineAPI;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    #endregion

    public static class QlikWebsocket
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        public static IDoc CreateNewConnection(SessionInfo sessionInfo, bool attached = false)
        {
            try
            {
                if (String.IsNullOrEmpty(sessionInfo.AppId))
                {
                    logger.Debug("No appid found.");
                    return null;
                }
                var url = ServerUtils.MakeWebSocketFromHttp(sessionInfo.ConnectUri);
                var connId = Guid.NewGuid().ToString();
                if (attached)
                    url = $"{url}/app/{sessionInfo.AppId}";
                else
                    url = $"{url}/app/{sessionInfo.AppId}/identity/{connId}";
                logger.Info($"Connect to: {url}");
                var config = new EnigmaConfigurations()
                {
                    Url = url,
                    CreateSocket = async (Url) =>
                    {
                        var webSocket = new ClientWebSocket();
                        webSocket.Options.RemoteCertificateValidationCallback = ValidationCallback.ValidateRemoteCertificate;
                        webSocket.Options.Cookies = new CookieContainer();
                        var cookie = sessionInfo.Cookie;
                        cookie.HttpOnly = false;
                        webSocket.Options.Cookies.Add(cookie);
                        webSocket.Options.KeepAliveInterval = TimeSpan.FromDays(48);
                        await webSocket.ConnectAsync(new Uri(Url), CancellationToken.None);
                        return webSocket;
                    },
                };
                var session = Enigma.Create(config);
                var globalTask = session.OpenAsync();
                globalTask.Wait();
                IGlobal global = Impromptu.ActLike<IGlobal>(globalTask.Result);
                var checkTask = global.IsDesktopModeAsync();
                checkTask.Wait(2000);
                if (!checkTask.IsCompletedSuccessfully)
                    throw new Exception("No active connection.");
                logger.Debug("Open qlik app.");
                if (attached)
                    return global.GetActiveDocAsync().Result;
                else
                    return global.OpenDocAsync(sessionInfo.AppId).Result;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Create a websocket connection was failed.");
                return null;
            }
        }
    }
}
