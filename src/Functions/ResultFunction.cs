namespace Ser.ConAai.Functions
{
    #region Usings
    using System;
    using System.Linq;
    using System.Text;
    using Newtonsoft.Json.Linq;
    using NLog;
    using Ser.ConAai.Communication;
    using Ser.ConAai.Config;
    #endregion

    public class ResultFunction : BaseFunction
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Constructor
        public ResultFunction(RuntimeOptions options) : base(options) { }
        #endregion

        #region Private Methods
        private string GetFormatedJsonForQlik(string distibuteJson)
        {
            var distibuteArray = JArray.Parse(distibuteJson);
            var resultText = new StringBuilder(">>>");
            resultText.Append($"{Environment.NewLine}{Environment.NewLine}{"Results".ToUpperInvariant()}:");
            resultText.Append($"{Environment.NewLine}-------------------------------------------------------------------");
            foreach (JObject item in distibuteArray)
            {
                var objChildren = item.Children();
                foreach (JProperty prop in objChildren)
                    resultText.Append($"{Environment.NewLine}{prop.Name.ToUpperInvariant()}: {prop.Value}");
                resultText.Append($"{Environment.NewLine}-------------------------------------------------------------------");
            }
            return resultText.ToString();
        }
        #endregion

        #region Public Methods
        public QlikResponse FormatJobResult(QlikRequest request)
        {
            var result = new QlikResponse();
            
            try
            {
                if (request.ManagedTaskId != null)
                {
                    logger.Debug($"Formatting for Qlik will be created by Task '{request.ManagedTaskId}'.");
                    var managedTaskId = new Guid(request.ManagedTaskId);
                    var resultQlikTask = Options.TaskPool.ManagedTasks.Values.ToList().FirstOrDefault(t => t.Id == managedTaskId);
                    if (resultQlikTask != null)
                    {
                        var distibuteJson = resultQlikTask.DistributeResult;
                        if (distibuteJson != null)
                        {
                            result.FormatedResult = GetFormatedJsonForQlik(distibuteJson);
                            logger.Debug("The delivery result was successfully formatted.");
                            return result;
                        }
                        else
                        {
                            logger.Warn("The delivery result is null.");
                        }
                    }
                    else
                    {
                        logger.Warn($"No managed task id '{managedTaskId}' for formated Qlik result found.");
                    }
                }
                else
                {
                    logger.Warn("No task id for formated Qlik result found.");
                }
            }
            catch (Exception ex)
            {
                result.SetErrorMessage(ex);
                logger.Error(ex, "The result function has an unknown error.");
            }
            return null;
        }
        #endregion
    }
}