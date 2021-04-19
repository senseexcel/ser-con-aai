namespace Ser.ConAai.Config
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using NLog;
    using Q2g.HelperQlik;
    using Q2g.HelperQrs;
    using Ser.Api.Model;
    using Ser.ConAai.Communication;
    #endregion

    public class SessionHelper
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Variables & Properties
        private readonly List<SessionInfo> Sessions = new List<SessionInfo>();
        private readonly object threadObject = new object();
        public JwtSessionManager Manager { get; private set; }
        #endregion

        #region Constructor
        public SessionHelper()
        {
            Manager = new JwtSessionManager();
        }
        #endregion

        #region Public Methods
        public static bool ValidateSession(SessionInfo sessionInfo)
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

        public SessionInfo GetSession(SerConnection connection, QlikRequest request)
        {
            try
            {
                lock (threadObject)
                {
                    var uri = connection.ServerUri;
                    var oldSession = Sessions?.FirstOrDefault(u => u.ConnectUri.OriginalString == uri.OriginalString
                                                          && u.User.ToString() == request.QlikUser.ToString()
                                                          && u.AppId == request.AppId) ?? null;
                    if (oldSession != null)
                    {
                        logger.Debug("Old session found...");
                        var result = ValidateSession(oldSession);
                        if (result)
                        {
                            logger.Debug("Old session is working...");
                            return oldSession;
                        }
                        logger.Debug("Old session not working, i remove it...");
                        Sessions.Remove(oldSession);
                    }
                }

                logger.Debug("Create new Websocket session...");
                var sessionInfo = Manager.CreateNewSession(connection, request.QlikUser, request.AppId);
                if (sessionInfo != null)
                    Sessions.Add(sessionInfo);
                return sessionInfo;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The session could not recreated or reused.");
                return null;
            }
        }
        #endregion
    }
}