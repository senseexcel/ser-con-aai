namespace Ser.ConAai
{
    #region Usings
    using Qlik.EngineAPI;
    using System;
    using NLog;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Net;
    using System.Net.Http;
    using Q2g.HelperQrs;
    using Newtonsoft.Json.Linq;
    #endregion

    public static class ScriptCheck
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region public methods
        public static bool DataLoadCheck(Uri serverUri, string scriptAppId, UserParameter parameter, SessionInfo info, int timeout)
        {
            try
            {
                if (scriptAppId == null)
                    return true;

                if (scriptAppId != parameter.AppId)
                {
                    logger.Debug("Reload - Normal mode");
                    return true;
                }

                var reloadTime = GetLastReloadTime(serverUri, parameter.ConnectCookie, scriptAppId);
                var tsConn = new CancellationTokenSource(timeout);
                var app = GetConnection(info, tsConn.Token);
                var ts = new CancellationTokenSource(timeout);
                if (app != null)
                {
                    logger.Debug("Reload - Attached session found.");
                    var result = TestCalc(app, ts.Token);
                    return true;
                }
                if (reloadTime == null)
                    return false;
                app = parameter.SocketConnection;
                if (reloadTime != null)
                {
                    logger.Debug("Reload - Wait for finish scripts.");
                    while (true)
                    {
                        Thread.Sleep(1000);
                        if (ts.Token.IsCancellationRequested)
                            return false;
                        var tempLoad = GetLastReloadTime(serverUri, parameter.ConnectCookie, scriptAppId);
                        if (tempLoad == null)
                            return false;
                        if (reloadTime.Value.Ticks < tempLoad.Value.Ticks)
                            return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Check reload script failed.");
                return false;
            }
        }
        #endregion

        #region private method
        private static DateTime? GetLastReloadTime(Uri serverUri, Cookie cookie, string appId)
        {
            try
            {
                var qrshub = new QlikQrsHub(serverUri, cookie);
                var qrsResult = qrshub.SendRequestAsync($"app/{appId}", HttpMethod.Get).Result;
                logger.Trace($"appResult:{qrsResult}");
                dynamic jObject = JObject.Parse(qrsResult);
                DateTime reloadTime = jObject?.lastReloadTime.ToObject<DateTime>() ?? null;
                return reloadTime;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The last reload time could not found.");
                return null;
            }
        }

        private static IDoc GetConnection(SessionInfo info, CancellationToken token)
        {
            try
            {
                var app = QlikWebsocket.CreateNewConnection(info, true);
                if (app != null)
                    return app;
                if (token.IsCancellationRequested)
                    return null;
                logger.Debug("No attched session found.");
                Thread.Sleep(2000);
                return GetConnection(info, token);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Reload - No attached session found.");
                return null;
            }
        }

        private static string TestCalc(IDoc app, CancellationToken token)
        {
            try
            {
                var result = app.EvaluateExAsync("1+1").Result;
                return result.qText;
            }
            catch (Exception ex)
            {
                Console.Write(ex.Message, "Could not do it");
                if (token.IsCancellationRequested)
                    return null;
                Thread.Sleep(2000);
                return TestCalc(app, token);
            }
        }
        #endregion
    }
}