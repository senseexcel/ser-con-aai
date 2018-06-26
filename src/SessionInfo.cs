namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Net;
    using Ser.Api;
    #endregion

    public class SessionInfo
    {
        #region Properties
        public Cookie Cookie { get; set; }
        public Uri ConnectUri { get; set; }
        public string AppId { get; set; }
        public DomainUser UserId { get; set; }
        #endregion
    }
}
