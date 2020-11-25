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
                    foreach (var managedTaskPair in ManagedTasks)
                    {
                        //Check for cancellation
                        if (CancelRunning())
                            return;

                        var managedTask = managedTaskPair.Value;

                        //Check if qlik aborted the communication
                        if (managedTask.LastQlikFunctionCall != null)
                        {
                            var timeSpan = DateTime.Now - managedTask.LastQlikFunctionCall.Value;
                            if (timeSpan.TotalSeconds > runtimeOptions.Config.StopTimeout)
                            {
                                var stopRequest = new QlikRequest
                                {
                                    ManagedTaskId = managedTask.Id.ToString()
                                };
                                var stopFunction = new StopFunction(runtimeOptions);
                                stopFunction.StopReportJobs(stopRequest, true);
                            }
                        }

                        //Check for cancellation
                        if (CancelRunning())
                            return;

                        var operationResult = runtimeOptions.RestClient.TaskWithIdAsync(managedTask.Id).Result;
                        if (operationResult.Success.Value)
                        {
                            jobResults = operationResult?.Results?.ToList() ?? new List<Ser.Engine.Rest.Client.JobResult>();
                            var runningResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.ABORT).ToList();
                            if (jobResults.Count == runningResults.Count)
                            {
                                //Find all success results
                                var successClientResults = jobResults.Where(r => r.Status == Engine.Rest.Client.JobResultStatus.SUCCESS).ToList();

                                //Download all success engine task result files
                                var successResults = ConvertApiType<List<JobResult>>(successClientResults);
                                DownloadResultFiles(managedTask.Id, successResults);

                                //Distibute all success results
                                Distibute(successResults, managedTask);
                                managedTask.Status = 3;

                                //All results that were not successful
                                var otherClientResults = jobResults.Where(r => r.Status != Engine.Rest.Client.JobResultStatus.SUCCESS).ToList();
                                var otherResults = ConvertApiType<List<JobResult>>(otherClientResults);

                                //Write all results
                                managedTask.JobResults.AddRange(successResults);
                                managedTask.JobResults.AddRange(otherResults);
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
                        if (managedTask.Status > 3 || managedTask.Status == -1)
                        {
                            CleanUp(managedTask);
                            continue;
                        }
                    }

                    //Wait for working Threads
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The managed task watching failed.", ex);
            }
        }

        private bool CancelRunning()
        {
            if (runtimeOptions.Cancellation.IsCancellationRequested)
            {
                logger.Debug("The watcher thread was canceled.");
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

        private void DownloadResultFiles(Guid taskId, List<JobResult> jobResults)
        {
            foreach (var jobResult in jobResults)
            {
                foreach (var jobReport in jobResult.Reports)
                {
                    foreach (var path in jobReport.Paths)
                    {
                        var filename = Path.GetFileName(path);
                        logger.Debug($"Download file {filename} form task {taskId}.");
                        var streamData = runtimeOptions.RestClient.DownloadFilesAsync(taskId, filename).Result;
                        if (streamData != null)
                        {
                            var buffer = GetStreamBuffer(streamData);
                            jobReport.Data.Add(new ReportData() { Filename = filename, DownloadData = buffer });
                        }
                        else
                            logger.Warn($"File {filename} for download not found.");
                    }
                }
            }
        }

        private void Distibute(List<JobResult> jobResults, ManagedTask task)
        {
            runtimeOptions.Analyser?.SetCheckPoint("CheckStatus", "Start Delivery of reports");
            task.Message = "Delivery Report, Please wait...";
            var distribute = new DistributeManager();
            var privateKeyPath = runtimeOptions.Config.Connection.Credentials.PrivateKey;
            var privateKeyFullname = HelperUtilities.GetFullPathFromApp(privateKeyPath);
            var distibuteOptions = new DistibuteOptions()
            {
                SessionUser = task.Session.User,
                CancelToken = task.Cancellation.Token,
                PrivateKeyPath = privateKeyFullname
            };
            var result = distribute.Run(jobResults, distibuteOptions);

            runtimeOptions.SessionHelper.Manager.MakeSocketFree(task?.Session ?? null);
            if (task.Cancellation.IsCancellationRequested)
            {
                task.Message = "The delivery was canceled by user.";
                return;
            }
    
            if (result != null)
            {
                logger.Debug($"Distribute result: {result}");
                task.DistributeResult = result;
                logger.Debug("The delivery was successfully.");
            }
            else
            {
                throw new Exception(distribute.ErrorMessage);
            }
            runtimeOptions.Analyser?.SetCheckPoint("CheckStatus", "Report(s) finished");
        }

        private void CleanUp(ManagedTask task)
        {
            try
            {
                Task.Delay(runtimeOptions.Config.CleanupTimeout)
                .ContinueWith((_) =>
                {
                    try
                    {
                        logger.Debug($"Run clean up process for folder and socket connection...");

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

                        logger.Debug($"Cleanup of the task '{task.Id}' has been completed.");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Cleaning up the task '{task.Id}' failed.");
                    }
                });
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
            runtimeOptions = options;
            var poolTask = Task.Factory.StartNew(() =>
            {
                WatchForFinishTasks();
            }, runtimeOptions.Cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            poolTask.Start();
        }
        #endregion
    }
}