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
    using Ser.Connections;
    using Ser.Api;
    #endregion

    public static class ScriptCheck
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region public methods
        public static bool DataLoadCheck(Uri serverUri, string scriptAppId, SessionInfo info, int timeout)
        {
            try
            {
                if (scriptAppId == null)
                    return true;

                if (timeout <= 0)
                    return true;

                var reloadTime = GetLastReloadTime(serverUri, info.Cookie, scriptAppId);
                var tsConn = new CancellationTokenSource(timeout);
                var session = GetConnection(info, tsConn.Token);
                var ts = new CancellationTokenSource(timeout);
                if (session != null)
                {
                    logger.Debug("Reload - Attached session found.");
                    var result = TestCalc(session.QlikConn.CurrentApp, ts.Token);
                    return true;
                }
                if (reloadTime == null)
                    return false;
                if (reloadTime != null)
                {
                    logger.Debug("Reload - Wait for finish scripts.");
                    while (true)
                    {
                        Thread.Sleep(1000);
                        if (ts.Token.IsCancellationRequested)
                            return false;
                        var taskStatus = GetTaskStatus(serverUri, info.Cookie, scriptAppId);
                        if (taskStatus != 2)
                            return true;
                        var tempLoad = GetLastReloadTime(serverUri, info.Cookie, scriptAppId);
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
        private static int GetTaskStatus(Uri serverUri, Cookie cookie, string appId)
        {
            try
            {
                var qrshub = new QlikQrsHub(serverUri, cookie);
                var qrsResult = qrshub.SendRequestAsync($"task/full", HttpMethod.Get).Result;
                logger.Trace($"taskResult:{qrsResult}");
                dynamic tasks = JArray.Parse(qrsResult);
                foreach (var task in tasks)
                    if (task.app.id == appId)
                        return task.operational.lastExecutionResult.status;
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The last reload time could not found.");
                return 0;
            }
        }

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

        private static SessionInfo GetConnection(SessionInfo session, CancellationToken token)
        {
            try
            {
                var attenchedConnection = session.QlikConn.Config;
                attenchedConnection.Identities = new List<string>() { "" };
                var qlikconn = ConnectionManager.NewConnection(attenchedConnection);
                if (qlikconn != null)
                    return session;
                if (token.IsCancellationRequested)
                    return null;
                logger.Debug("No attched session found.");
                Thread.Sleep(2000);
                return GetConnection(session, token);
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