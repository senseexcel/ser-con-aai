namespace Ser.ConAai.Communication
{
    #region Usings
    using System;
    using System.Text;
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
        public string TaskId { get; set; }
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

        public void SetErrorMessage(Exception exception)
        {
            try
            {
                var x = exception?.InnerException ?? null;
                var msg = new StringBuilder(exception?.Message);
                while (x != null)
                {
                    msg.Append($"{Environment.NewLine}{x.Message}");
                    x = x.InnerException;
                }
                Log = msg.ToString()?.Replace("\r\n", " -> ");
            }
            catch (Exception ex)
            {
                Log = $"The error message could not be merged. Error: '{ex.Message}'.";
            }
        }
        #endregion
    }
}