namespace Ser.ConAai.Functions
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using NLog;
    using Ser.ConAai.Communication;
    using Ser.ConAai.Config;
    using Ser.ConAai.TaskObjects;
    #endregion

    #region Constructor
    public class StopFunction : BaseFunction
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Constructor
        public StopFunction(RuntimeOptions options) : base(options) { }
        #endregion

        #region Private Methods
        private void StopReportJob(QlikRequest request, bool isTimeout = false)
        {
            var taskId = new Guid(request.ManagedTaskId);
            var managedStopTask = Options.TaskPool.ManagedTasks.Values.FirstOrDefault(t => t.Id == taskId);
            if (managedStopTask != null && managedStopTask.Status != 4)
            {
                logger.Debug($"Stopping task id '{managedStopTask.Id}'...");
                var stopResult = Options.RestClient.StopTasksAsync(managedStopTask.Id).Result;
                if (stopResult.Success.Value)
                {
                    logger.Debug($"The managed task '{managedStopTask.Id}' was stopped.");
                    if (isTimeout)
                        managedStopTask.Message = "The report job was canceled by timeout.";
                    else
                        managedStopTask.Message = "The task was aborted by user.";
                    managedStopTask.Status = 4;
                    managedStopTask.Cancellation.Cancel();
                }
                else
                {
                    logger.Warn($"The managed task '{managedStopTask.Id}' could not stopped.");
                }
            }
            else
            {
                logger.Warn($"No job found with the Id '{managedStopTask.Id}' to stop.");
            }
        }
        #endregion

        #region Public Methods
        public void StopReportJobs(QlikRequest request, bool isTimeout = false)
        {
            Task.Run(() =>
            {
                try
                {
                    if (request.ManagedTaskId == "all")
                    {
                        logger.Debug("All tasks will be stopped...");
                        if (Options.TaskPool.ManagedTasks.Count == 0)
                            logger.Warn("No stopping jobs.");

                        var managedTasks = Options.TaskPool.ManagedTasks?.Values?.ToList() ?? new List<ManagedTask>();
                        foreach (var managedTask in managedTasks)
                        {
                            if (managedTask.Status == 1 || managedTask.Status == 2)
                            {
                                var stopResult = Options.RestClient.StopTasksAsync(managedTask.Id).Result;
                                if (stopResult.Success.Value)
                                {
                                    if (isTimeout)
                                        managedTask.Message = "The report job was canceled by timeout.";
                                    else
                                        managedTask.Message = "The task was aborted by user.";
                                    managedTask.Status = 4;
                                    managedTask.Cancellation.Cancel();
                                }
                                else
                                {
                                    logger.Warn("The jobs could not be stopped.");
                                    managedTask.Message = "The jobs could not be stopped.";
                                }
                            }
                        }
                    }
                    else if (request.ManagedTaskId != null)
                    {
                        logger.Debug($"Managed task '{request.ManagedTaskId}' will be stopped...");
                        StopReportJob(request, isTimeout);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "The stop function has an unknown error.");
                }
            }, Options.Cancellation.Token);
        }
        #endregion

    }
    #endregion
}