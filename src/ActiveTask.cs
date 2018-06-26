namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ActiveTask
    {
        public int ProcessId { get; set; }
        public string DownloadLink { get; set; }
        public int Status { get; set; }
        public string Id { get; set; }
        public DateTime StartTime { get; set; }
        public string AppId { get; set; }
        public DomainUser UserId { get; set; }
        public SessionInfo Session { get; set; }
    }
}