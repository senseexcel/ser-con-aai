namespace Ser.ConAai.Communication
{
    #region Usings
    using System;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json.Serialization;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class QlikResponse
    {
        #region Properties
        public int Status { get; set; }
        public Guid? TaskId { get; set; }
        public string Log { get; set; }
        public string Distribute { get; set; }
        public string FormatedResult { get; set; }
        public string Version { get; set; }
        public string ExternalPackagesInfo { get; set; }
        public JArray ManagedTasks { get; set; } = new JArray();
        public JArray JobResults { get; set; } = new JArray();
        #endregion

        #region Public Methods
        public override string ToString()
        {
            return $"{Status}";
        }
        #endregion
    }
}