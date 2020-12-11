namespace Ser.ConAai.TaskObjects
{
    #region Usings
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using NLog;
    using Q2g.HelperQlik;
    using Ser.Api;
    using Ser.ConAai.Communication;
    using Ser.ConAai.Config;
    using Ser.ConAai.Functions;
    using Ser.Distribute;
    #endregion

    public class ManagedTaskPool
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties && Variables
        private RuntimeOptions runtimeOptions;
        private int errorCount = 0;
        public ConcurrentDictionary<Guid, ManagedTask> ManagedTasks { get; }
        #endregion

        #region Constructor
        public ManagedTaskPool()
        {
            ManagedTasks = new ConcurrentDictionary<Guid, ManagedTask>();
        }
        #endregion

        #region Private Methods
        private void WatchForFinishTasks()
        {
            try
            {
                while (true)
                {
                    //Check for cancellation
                    if (CancelRunning())
                        return;

                    var jobResults = new List<Ser.Engine.Rest.Client.JobResult>();
                    var managedPoolTasks = ManagedTasks.Values.ToList();
                    foreach (var managedTask in managedPoolTasks)
                    {
                        //Check for cancellation
                        if (CancelRunning())
                            return;

                        //Ignore tasks that are already marked for deletion
                        if (managedTask.InternalStatus == InternalTaskStatus.CLEANUP)
                            continue;

                        //Clean up stopped tasks
                        if (managedTask.InternalStatus == InternalTaskStatus.STOPEND ||
                            managedTask.InternalStatus == InternalTaskStatus.ERROR)
                        {
                            CleanUp(managedTask);
                            continue;
                        }

                        //Check if qlik aborted the communication
                        if (managedTask.LastQlikFunctionCall != null)
                        {
                            var timeSpan = DateTime.Now - managedTask.LastQlikFunctionCall.Value;
                            if (timeSpan.TotalSeconds > runtimeOptions.Config.StopTimeout)
                            {
                                if (managedTask.InternalStatus == InternalTaskStatus.ENGINEISRUNNING ||
                                    managedTask.InternalStatus == InternalTaskStatus.DISTRIBUTESTART)
                                    StopManagedTask(managedTask);
                            }
                        }

                        //Check for cancellation
                        if (CancelRunning())
                            return;

                        var operationResult = runtimeOptions.RestClient.TaskWithIdAsync(managedTask.Id).Result;
                        if (operationResult.Success.Value)
                        {
                            jobResults = operationResult?.Results?.ToList() ?? new List<Ser.Engine.Rest.Client.JobResult>();
                            if (jobResults.Count == 0)
                                continue;

                            var runningResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.ABORT).ToList();
                            if (runningResults.Count > 0)
                            {
                                managedTask.Message = "Report job is running...";
                                managedTask.Status = 1;
                                managedTask.InternalStatus = InternalTaskStatus.ENGINEISRUNNING;
                                continue;
                            }

                            if (managedTask.InternalStatus == InternalTaskStatus.DISTRIBUTESTART)
                                continue;

                            if (managedTask.InternalStatus == InternalTaskStatus.CREATEREPORTJOBEND || 
                                managedTask.InternalStatus == InternalTaskStatus.ENGINEISRUNNING)
                            {
                                var convertedResults = ConvertApiType<List<JobResult>>(jobResults);
                                managedTask.JobResults.AddRange(convertedResults);

                                //Download all success engine task result files
                                if (managedTask.InternalStatus == InternalTaskStatus.ENGINEISRUNNING)
                                    DownloadResultFiles(managedTask);

                                //Distibute all results
                                Distibute(managedTask);
                            }
                        }
                        else
                        {
                            logger.Warn("The response of the operation job result was wrong.");
                        }

                        //Check for cancellation
                        if (CancelRunning())
                            return;

                        //Clean up tasks that are done.
                        if (managedTask.InternalStatus != InternalTaskStatus.CLEANUP)
                            CleanUp(managedTask);
                    }

                    //Wait for working Threads
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The managed task watching failed.", ex);

                errorCount++;
                if (errorCount <= 5)
                    WatchForFinishTasks();
            }
        }

        private void StopManagedTask(ManagedTask task)
        {
            task.InternalStatus = InternalTaskStatus.STOPSTART;
            var stopRequest = new QlikRequest
            {
                ManagedTaskId = task.Id.ToString()
            };
            var stopFunction = new StopFunction(runtimeOptions);
            stopFunction.StopReportJobs(stopRequest, true);
        }

        private bool CancelRunning()
        {
            if (runtimeOptions.Cancellation.IsCancellationRequested)
            {
                logger.Info("The watcher thread was canceled.");
                return true;
            }
            return false;
        }

        private T ConvertApiType<T>(object value)
        {
            try
            {
                var json = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Convert type failed.");
                return default;
            }
        }

        private byte[] GetStreamBuffer(Engine.Rest.Client.FileResponse result)
        {
            List<byte> bufferList = new List<byte>();
            var contentLength = result.Headers["Content-Length"].FirstOrDefault();
            if (contentLength != null)
            {
                var bufferLength = Convert.ToInt32(contentLength);
                var readLength = -1;
                while (readLength != 0)
                {
                    var buffer = new byte[bufferLength];
                    readLength = result.Stream.Read(buffer, 0, bufferLength);
                    bufferList.AddRange(buffer.Take(readLength));
                }
                return bufferList.ToArray();
            }
            else
            {
                var mem = new MemoryStream();
                result.Stream.CopyTo(mem);
                var buffer = mem?.GetBuffer() ?? null;
                bufferList.AddRange(buffer);
            }
            return bufferList.ToArray();
        }

        private void DownloadResultFiles(ManagedTask task)
        {
            try
            {
                runtimeOptions.Analyser?.SetCheckPoint("DownloadResultFiles", $"Start - Download files");
                foreach (var jobResult in task.JobResults)
                {
                    if (jobResult.Status == TaskStatusInfo.SUCCESS || jobResult.Status == TaskStatusInfo.WARNING)
                    {
                        foreach (var jobReport in jobResult.Reports)
                        {
                            foreach (var path in jobReport.Paths)
                            {
                                var filename = Path.GetFileName(path);
                                logger.Debug($"Download file '{filename}' form task '{task.Id}'...");
                                var streamData = runtimeOptions.RestClient.DownloadFilesAsync(task.Id, filename).Result;
                                if (streamData != null)
                                {
                                    var buffer = GetStreamBuffer(streamData);
                                    jobReport.Data.Add(new ReportData() { Filename = filename, DownloadData = buffer });
                                }
                                else
                                    logger.Warn($"File '{filename}' for download not found.");
                            }
                        }
                    }
                }
                runtimeOptions.Analyser?.SetCheckPoint("DownloadResultFiles", $"End - Download files");
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "The method 'DownloadResultFiles' failed.");
                task.Status = -1;
                task.InternalStatus = InternalTaskStatus.ERROR;
                task.Error = ex;
            }
        }

        private void Distibute(ManagedTask task)
        {
            Task.Run(() =>
            {
                try
                {
                    task.InternalStatus = InternalTaskStatus.DISTRIBUTESTART;
                    runtimeOptions.Analyser?.SetCheckPoint("Distibute", "Start - Delivery of reports");
                    task.Status = 2;
                    task.Message = "Report job is distributed...";
                    var distribute = new DistributeManager();
                    var privateKeyPath = runtimeOptions.Config.Connection.Credentials.PrivateKey;
                    var privateKeyFullname = HelperUtilities.GetFullPathFromApp(privateKeyPath);
                    var distibuteOptions = new DistibuteOptions()
                    {
                        SessionUser = task.Session.User,
                        CancelToken = task.Cancellation.Token,
                        PrivateKeyPath = privateKeyFullname
                    };
                    var result = distribute.Run(task.JobResults, distibuteOptions);
                    runtimeOptions.SessionHelper.Manager.MakeSocketFree(task.Session);
                    if (task.Cancellation.IsCancellationRequested)
                    {
                        task.Message = "The delivery was canceled by user.";
                        return;
                    }

                    if (result != null)
                    {
                        logger.Debug($"Distribute result: '{result}'");
                        task.DistributeResult = result;
                        logger.Debug("The delivery was successfully.");
                        task.Endtime = DateTime.Now;
                        task.Status = 3;
                    }
                    else
                    {
                        throw new Exception(distribute.ErrorMessage);
                    }
                    runtimeOptions.Analyser?.SetCheckPoint("Distibute", "End - Delivery of reports");
                    task.InternalStatus = InternalTaskStatus.DISTRIBUTEEND;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"The managed task '{task.Id}' could not be distibuted.");
                    task.Error = ex;
                    task.Status = -1;
                    task.InternalStatus = InternalTaskStatus.ERROR;
                }
            });
        }

        private void CleanUp(ManagedTask task)
        {
            try
            {
                if (task.Status > 2 || task.Status == -1)
                {
                    task.InternalStatus = InternalTaskStatus.CLEANUP;
                    task.Endtime = DateTime.Now;

                    Task.Delay(runtimeOptions.Config.CleanupTimeout * 1000)
                    .ContinueWith((_) =>
                    {
                        try
                        {
                            logger.Debug($"Run clean up process...");
                            runtimeOptions.Analyser?.SetCheckPoint("CleanUp", "Start - Cleanup");

                            if (ManagedTasks.TryRemove(task.Id, out var taskResult))
                                logger.Debug($"Managed task '{task.Id}' was successfully removed from the managed pool.");
                            else
                                logger.Debug($"Managed task '{task.Id}' could not be removed from the pool.");

                            runtimeOptions.SessionHelper.Manager.MakeSocketFree(task?.Session ?? null);

                            var deleteResult = runtimeOptions.RestClient.DeleteFilesAsync(task.Id).Result;
                            if (deleteResult.Success.Value)
                                logger.Debug($"The directory for the task '{task.Id}' was successfully deleted.");
                            else
                                logger.Warn($"The directory for the task '{task.Id}' could not be deleted.");

                            foreach (var guidItem in task.FileUploadIds)
                            {
                                deleteResult = runtimeOptions.RestClient.DeleteFilesAsync(guidItem).Result;
                                if (deleteResult.Success.Value)
                                    logger.Debug($"The upload directory '{guidItem}' of the task was successfully deleted.");
                                else
                                    logger.Warn($"The upload directory '{guidItem}' of the task could not be deleted.");
                            }

                            runtimeOptions.Analyser?.SetCheckPoint("CleanUp", "End - Cleanup");
                            logger.Debug($"Cleanup of the task '{task.Id}' has been completed.");
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, $"Cleaning up the task '{task.Id}' failed.");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The cleanup has an unknown error.");
            }
        }
        #endregion

        #region Public Methods
        public void Run(RuntimeOptions options)
        {
            logger.Info("Managed task pool is running...");
            runtimeOptions = options;
            var poolTask = Task.Factory.StartNew(() =>
            {
                WatchForFinishTasks();
            }, runtimeOptions.Cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        #endregion
    }
}