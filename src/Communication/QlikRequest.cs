namespace Ser.ConAai.Communication
{
    #region Usings
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using Hjson;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json.Serialization;
    using NLog;
    using Q2g.HelperQrs;
    using Ser.Api;
    using YamlDotNet.Serialization;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class QlikRequest
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties
        public string ManagedTaskId { get; set; }
        public string VersionMode { get; set; }
        public DomainUser QlikUser { get; set; }
        public string AppId { get; set; }
        public string JsonScript { get; set; }
        public static Exception Error { get; set; }
        #endregion

        #region Private Methods
        private static bool IsJsonScript(string userJson)
        {
            var checkString = userJson.Replace("\r\n", "\n").ToLowerInvariant();
            if (Regex.IsMatch(checkString, "connections:[ \t]*\n[ \t]*{", RegexOptions.Singleline))
                return true;
            if (userJson.StartsWith("{\""))
                return true;
            if (userJson == "versions: all")
                return true;
            return false;
        }

        private static string ConvertYamlToJson(string yaml)
        {
            try
            {
                if (String.IsNullOrEmpty(yaml))
                    return null;

                using TextReader sr = new StringReader(yaml);
                var deserializer = new Deserializer();
                var yamlConfig = deserializer.Deserialize(sr);
                return JsonConvert.SerializeObject(yamlConfig);
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not normalize yaml, please check your script. Error: {ex.Message}");
            }
        }
        #endregion

        #region Public Methods
        public static QlikRequest Parse(DomainUser qlikUser, string appId, string jsonScript)
        {
            try
            {
                logger.Debug($"Parse script '{jsonScript}'...");
                jsonScript = jsonScript.ToString();
                if (IsJsonScript(jsonScript))
                {
                    //Parse HJSON or JSON
                    logger.Debug("HJSON or JSON detected...");
                    jsonScript = HjsonValue.Parse(jsonScript).ToString();
                }
                else
                {
                    //YAML
                    logger.Debug("YAML detected...");
                    jsonScript = ConvertYamlToJson(jsonScript);
                }

                if (jsonScript == null)
                    throw new Exception("The script could not be read.");

                logger.Debug("Reading script properties...");
                dynamic jsonObject = JObject.Parse(jsonScript);
                return new QlikRequest()
                {
                    QlikUser = qlikUser,
                    AppId = appId,
                    JsonScript = jsonScript,
                    ManagedTaskId = jsonObject?.taskId ?? null,
                    VersionMode = jsonObject?.versions ?? null
                };
            }
            catch (Exception ex)
            {
                Error = new Exception("The request is incorrect and could not be parsed.", ex);
                throw Error;
            }
        }

        public DomainUser GetAppOwner(QlikQrsHub qrsHub, string appId)
        {
            DomainUser resultUser = null;
            try
            {
                var qrsResult = qrsHub.SendRequestAsync($"app/{appId}", HttpMethod.Get).Result;
                logger.Trace($"appResult:{qrsResult}");
                if (qrsResult == null)
                    throw new Exception($"The result of the QRS request 'app/{appId}' of is null.");
                dynamic jObject = JObject.Parse(qrsResult);
                string userDirectory = jObject?.owner?.userDirectory ?? null;
                string userId = jObject?.owner?.userId ?? null;
                if (!String.IsNullOrEmpty(userDirectory) && !String.IsNullOrEmpty(userId))
                {
                    resultUser = new DomainUser($"{userDirectory}\\{userId}");
                    logger.Debug($"Found app owner: {resultUser}");
                }
                else
                    logger.Error($"No user directory {userDirectory} or user id {userId} found.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"The owner of the app {appId} could not found.");
            }
            return resultUser;
        }
        #endregion
    }
}