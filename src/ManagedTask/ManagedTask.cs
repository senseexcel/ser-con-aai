namespace Ser.ConAai.TaskObjects
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using System.Threading;
    using Q2g.HelperQlik;
    using Ser.Api;
    using System.Text;
    #endregion

    public enum InternalTaskStatus
    {
        CREATEREPORTJOBSTART,
        CREATEREPORTJOBEND,
        ENGINEISRUNNING,
        DOWNLOADFILESSTART,
        DOWNLOADFILESEND,
        DISTRIBUTESTART,
        DISTRIBUTEEND,
        ERROR
    }

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ManagedTask
    {
        #region Properties
        public Guid Id { get; }
        public DateTime StartTime { get; set; }
        public DateTime Endtime { get; set; }
        public DateTime? LastQlikFunctionCall { get; set; }
        public int Status { get; set; }
        public InternalTaskStatus InternalStatus { get; set; }
        public string DistributeResult { get; set; }
        public SessionInfo Session { get; set; }
        public string Message { get; set; }
        public Exception Error { get; set; }
        public CancellationTokenSource Cancellation { get; set; }
        public List<Guid> FileUploadIds { get; set; } = new List<Guid>();
        public JObject JobScript { get; set; }
        public List<JobResult> JobResults { get; set; }
        #endregion

        #region Constructor
        public ManagedTask()
        {
            Id = Guid.NewGuid();
        }
        #endregion

        #region Public Methods
        public string GetCompleteErrorMessage()
        {
            var x = Error?.InnerException ?? null;
            var msg = new StringBuilder(Error?.Message);
            while (x != null)
            {
                msg.Append($"{Environment.NewLine}{x.Message}");
                x = x.InnerException;
            }
            return msg.ToString()?.Replace("\r\n", " -> ");
        }
        #endregion
    }
}