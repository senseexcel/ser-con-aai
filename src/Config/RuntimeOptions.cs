namespace Ser.ConAai.Config
{
    #region Usings
    using System;
    using System.Threading;
    using Ser.ConAai.TaskObjects;
    using Ser.Diagnostics;
    using Ser.Engine.Rest.Client;
    #endregion

    public class RuntimeOptions
    {
        #region Properties
        public PerfomanceAnalyser Analyser { get; set; }
        public ReportingRestApiClient RestClient { get; set; }
        public SessionHelper SessionHelper { get; set; }
        public ConnectorConfig Config { get; set; }
        public CancellationTokenSource Cancellation { get; set; }
        public ManagedTaskPool TaskPool { get; set; }
        #endregion
    }
}