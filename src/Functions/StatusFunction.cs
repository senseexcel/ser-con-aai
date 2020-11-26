namespace Ser.ConAai.Functions
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using Newtonsoft.Json.Linq;
    using NLog;
    using Q2g.HelperQrs;
    using Ser.Api;
    using Ser.ConAai.Communication;
    using Ser.ConAai.Config;
    using Ser.ConAai.TaskObjects;
    #endregion

    public class StatusFunction : BaseFunction
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Constructor
        public StatusFunction(RuntimeOptions options) : base(options) { }
        #endregion

        #region Private Methods
        private JObject CreateTaskResult(ManagedTask task)
        {
            return JObject.FromObject(new
            {
                startTime = task.StartTime,
                status = task.Status,
                taskId = task.Id,
                appId = task.Session.AppId,
                userId = task.Session.User.ToString()
            });
        }

        private JObject CreateAuditMatrixParameters(DomainUser user)
        {
            return JObject.FromObject(new
            {
                resourceType = "App",
                resourceRef = new { },
                subjectRef = new
                {
                    resourceFilter = $"userid eq '{user.UserId}' and userdirectory eq '{user.UserDirectory}'"
                },
                actions = 46,
                environmentAttributes = "",
                subjectProperties = new string[] { "id", "name", "userId", "userDirectory" },
                auditLimit = 1000,
                outputObjectsPrivileges = 2
            });
        }

        private bool HasReadRights(QlikQrsHub qrsHub, ManagedTask task)
        {
            try
            {
                logger.Debug($"Search for read right of user {task.Session.User}");
                var parameter = CreateAuditMatrixParameters(task.Session.User);
                var contentData = Encoding.UTF8.GetBytes(parameter.ToString());
                var data = new ContentData()
                {
                    ContentType = "application/json",
                    FileData = contentData,
                };
                var matrixResponse = qrsHub.SendRequestAsync("systemrule/security/audit/matrix", HttpMethod.Post, data).Result;
                var responseObject = JObject.Parse(matrixResponse);
                var matrix = responseObject["matrix"]?.FirstOrDefault(m => m["resourceId"]?.ToString() == task.Session.AppId) ?? null;
                if (matrix != null)
                {
                    var privileges = responseObject["resources"][matrix["resourceId"]?.ToObject<string>()]["privileges"].ToList() ?? new List<JToken>();
                    var canAppRead = privileges?.FirstOrDefault(p => p?.ToObject<string>() == "read") ?? null;
                    if (canAppRead != null)
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The read rights of the user could not found.");
                return false;
            }
        }

        private JArray GetAllowedTasks(List<ManagedTask> activeTasks, QlikRequest request)
        {
            var results = new JArray();
            try
            {
                var session = Options.SessionHelper.GetSession(Options.Config.Connection, request);
                var qrsHub = new QlikQrsHub(session.ConnectUri, session.Cookie);
                foreach (var activeTask in activeTasks)
                {
                    if (activeTask.Status == 4)
                        continue;

                    var appOwner = request.GetAppOwner(qrsHub, activeTask.Session.AppId);
                    if (appOwner != null && appOwner.ToString() == activeTask.Session.User.ToString())
                    {
                        logger.Debug($"The app owner '{appOwner}' was found.");
                        results.Add(CreateTaskResult(activeTask));
                    }
                    else
                    {
                        if (HasReadRights(qrsHub, activeTask))
                            results.Add(CreateTaskResult(activeTask));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The list of tasks could not be determined.");
            }
            return results;
        }
        #endregion

        #region Public Methods
        public QlikResponse GetStatusResponse(QlikRequest request)
        {
            var result = new QlikResponse();
            try
            {
                if (request.VersionMode == "all")
                {
                    logger.Debug("Status - Read main version...");
                    result.Version = Options.Config.PackageVersion;
                    return result;
                }

                if (request.VersionMode == "packages")
                {
                    logger.Debug("Status - Read external package versions...");
                    result.ExternalPackagesInfo = Options.Config.ExternalPackageJson;
                    return result;
                }

                if (request.ManagedTaskId == "all")
                {
                    logger.Debug("Status - Get all managed tasks.");
                    var activeTasks = Options.TaskPool.ManagedTasks.Values?.ToList() ?? new List<ManagedTask>();
                    result.ManagedTasks = GetAllowedTasks(activeTasks, request);
                }
                else if (request.ManagedTaskId != null)
                {
                    logger.Debug($"Status - Get managed task status from id '{request.ManagedTaskId}'.");
                    var managedTaskId = new Guid(request.ManagedTaskId);
                    var currentTask = Options.TaskPool.ManagedTasks.Values.ToList().FirstOrDefault(t => t.Id == managedTaskId);
                    if (currentTask != null)
                    {
                        result.Distribute = currentTask.DistributeResult;
                        result.Status = currentTask.Status;
                        if (currentTask.Error != null)
                            result.SetErrorMessage(currentTask.Error);
                        else
                            result.Log = currentTask.Message;
                        currentTask.LastQlikFunctionCall = DateTime.Now;
                    }
                    else
                    {
                        logger.Warn($"Status - No managed task id '{request.ManagedTaskId}' in pool found.");
                        result.Log = "Status information is not available.";
                        result.Status = -1;
                    }
                }
                else
                {
                    logger.Warn("Status - No managed tasks with 'all' or 'id' found.");
                    result.Log = "Status information is not available.";
                    result.Status = -1;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The status function has an unknown error.");
            }
            return result;
        }
        #endregion
    }
}