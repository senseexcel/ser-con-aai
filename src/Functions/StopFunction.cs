﻿namespace Ser.ConAai.Functions
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
        private void StopReportJob(Guid taskId, bool isTimeout = false)
        {
            var managedStopTask = Options.TaskPool.ManagedTasks.Values.FirstOrDefault(t => t.Id == taskId);
            if (managedStopTask != null &&  managedStopTask.Status != 4 && managedStopTask?.Id != null)
            {
                Options.Analyser?.SetCheckPoint("StopReportJob", $"Stop managed task {managedStopTask.Id} starts");
                managedStopTask.InternalStatus = InternalTaskStatus.STOPSTART;
                logger.Debug($"Stopping task id '{managedStopTask.Id}'...");
                var stopResult = Options.RestClient.StopTask(managedStopTask.Id);
                if (stopResult)
                {
                    logger.Debug($"The managed task '{managedStopTask.Id}' was stopped.");
                    if (isTimeout)
                    {
                        logger.Info("The report job was canceled by timeout.");
                        managedStopTask.Message = "The report job was canceled by timeout.";
                    }
                    else
                    {
                        logger.Info("The task was aborted by user.");
                        managedStopTask.Message = "The task was aborted by user.";
                    }
                    managedStopTask.Status = 4;
                    managedStopTask.Cancellation.Cancel();
                }
                else
                {
                    logger.Warn($"The managed task '{managedStopTask.Id}' could not stopped.");
                }
                Options.Analyser?.SetCheckPoint("StopReportJob", $"Stop managed task {managedStopTask.Id} ends");
                managedStopTask.InternalStatus = InternalTaskStatus.STOPEND;
            }
            else
            {
                logger.Warn($"No job was found with the id '{managedStopTask?.Id}' to stop.");
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
                        if (Options.TaskPool.ManagedTasks.IsEmpty)
                            logger.Warn("No stopping jobs.");

                        var managedTasks = Options.TaskPool.ManagedTasks?.Values?.ToList() ?? new List<ManagedTask>();
                        foreach (var managedTask in managedTasks)
                        {
                            if (managedTask.Status >= 0 && managedTask.Status <= 2)
                                StopReportJob(managedTask.Id, isTimeout);
                            else
                                logger.Warn($"The task '{managedTask.Id}' has a status that cannot be stopped.");
                        }
                    }
                    else if (request.ManagedTaskId != null)
                    {
                        logger.Debug($"Managed task '{request.ManagedTaskId}' will be stopped...");
                        StopReportJob(new Guid(request.ManagedTaskId), isTimeout);
                    }
                    else
                    {
                        logger.Warn($"The task id of the task to be stopped is empty.");
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