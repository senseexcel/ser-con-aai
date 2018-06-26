namespace Ser.ConAai
{  
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class OnDemandResult
    {
        #region Properties
        public int Status { get; set; }
        public string TaskId { get; set; }
        public string Link { get; set; }
        public string Log { get; set; }
        public List<ActiveTask> Tasks { get; set; }
        public List<VersionInfo> Versions { get; set; }
        #endregion

        public override string ToString()
        {
            return $"{Status}";
        }
    }
}