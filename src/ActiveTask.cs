namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using System.Threading;
    using Q2g.HelperQlik;
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
        public DateTime? LastQlikCall { get; set; }
        public CancellationTokenSource CancelSource { get; set; }
    }
}