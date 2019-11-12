namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using Qlik.EngineAPI;
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using enigma;
    using Newtonsoft.Json.Linq;
    using System.Threading;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ActiveTask
    {
        public string Distribute { get; set; }
        public int Status { get; set; }
        public Guid Id { get; set; }
        public DateTime StartTime { get; set; }
        public SessionInfo Session { get; set; }
        public string Message { get; set; }
        public List<Guid> FileUploadIds { get; set; } = new List<Guid>();
        public JObject JobJson { get; set; }
        public bool Stoppable { get; set; }
        public bool Stopped { get; set; }
        public CancellationTokenSource CancelSource { get; set; }
    }
}