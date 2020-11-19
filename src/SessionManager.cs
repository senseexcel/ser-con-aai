namespace Ser.ConAai
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

                var sessionInfo = Manager.CreateNewSession(connection, qlikUser, appId);
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