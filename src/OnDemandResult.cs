namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json.Serialization;
    using Ser.Api;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class OnDemandResult
    {
        #region Properties
        public int Status { get; set; }
        public Guid? TaskId { get; set; }
        public string Log { get; set; }
        public string Distribute { get; set; }
        public string FormatedResult { get; set; }
        public JArray Tasks { get; set; }
        public string Version { get; set; }
        public string ExternalPackagesInfo { get; set; }
        #endregion

        public override string ToString()
        {
            return $"{Status}";
        }
    }
}