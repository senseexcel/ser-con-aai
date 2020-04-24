namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using NLog;
    #endregion

    public class JsonConfigResolver
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties && Variables
        public bool ResolveFormula { get; set; } = true;
        public bool ResolvePlaceholder { get; set; } = true;
        public string JsonConfig { get; set; }
        private string PropertyName { get; set; }
        private JsonPathBuilder jsonBuilder;
        private JObject jsonObjectConfig;
        private JObject newJsonObjectConfig;
        #endregion

        #region Constructor
        public JsonConfigResolver(string jsonConfig)
        {
            jsonObjectConfig = JsonConvert.DeserializeObject<JObject>(jsonConfig);
            newJsonObjectConfig = JObject.FromObject(jsonObjectConfig);
        }
        #endregion

        #region Private Methods
        private JToken GetParentToken()
        {
            var preJsonPath = jsonBuilder.GetPrePath();
            var parentToken = newJsonObjectConfig.SelectToken(preJsonPath);
            return parentToken;
        }

        private void ResolveToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    jsonBuilder.RemovePath(token.Path);
                    if (token.Children().Count() > 0)
                        InternalResolve(token);
                    break;
                case JTokenType.Property:
                    PropertyName = (token as JProperty).Name;
                    jsonBuilder.AppendPath(token.Path);
                    InternalResolve(token);
                    jsonBuilder.RemovePath(token.Path);
                    break;
                case JTokenType.String:
                    var strValue = token.Value<string>();
                    if (strValue == $"@{PropertyName}@")
                    {
                        var parentToken = GetParentToken();
                        var replaceToken = newJsonObjectConfig.SelectToken(jsonBuilder.ToString());
                        replaceToken.Replace(parentToken);
                    }
                    else if (strValue.StartsWith("="))
                    {
                        var newValue = strValue.Replace("$@(", "$(");
                        var replaceToken = newJsonObjectConfig.SelectToken(jsonBuilder.ToString());
                        replaceToken.Replace(newValue);
                    }
                    break;
                default:
                    break;
            }
        }

        private void InternalResolve(JToken jsonObject)
        {
            if (jsonObject.HasValues)
            {
                var children = jsonObject.Children().ToList();
                if (children.Count > 0)
                {
                    foreach (var token in children)
                    {
                        if (token.Type == JTokenType.Array)
                        {
                            var jarray = token.ToObject<JArray>();
                            foreach (var arrayToken in jarray.Children())
                                InternalResolve(arrayToken);
                        }
                        else
                        {
                            ResolveToken(token);
                        }
                    }
                }
            }
            else
                ResolveToken(jsonObject);
        }
        #endregion

        #region Public Methods
        public string Resolve()
        {
            try
            {
                jsonBuilder = new JsonPathBuilder();
                InternalResolve(jsonObjectConfig);
                return JsonConvert.SerializeObject(newJsonObjectConfig, Formatting.Indented);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "The json structur could not resolve.");
                return null;
            }
        }
        #endregion
    }

    #region Helper Classes
    public class JsonPathBuilder
    {
        #region Properties
        public List<JsonPathSegment> Segments { get; private set; } = new List<JsonPathSegment>();
        public string FullPath { get; private set; }
        #endregion

        #region Constructor
        public JsonPathBuilder(string fullPath)
        {
            FullPath = fullPath;
            ReadPath();
        }

        public JsonPathBuilder()
        {
            FullPath = "$.";
        }
        #endregion

        #region Private Methods
        private void ReadPath()
        {
            Segments.Clear();
            var splitResult = FullPath.Split('.');
            foreach (var item in splitResult)
                Segments.Add(CreateSegment(item));
        }

        private JsonPathSegment CreateSegment(string segmentValue)
        {
            var segment = new JsonPathSegment();
            var arrayAfter = Regex.Match(segmentValue, "^([A-Za-z]+)\\[([0-9]+)\\]$");
            segment.Name = segmentValue;
            if (arrayAfter.Success)
            {
                segment.Name = arrayAfter.Groups[1].Value;
                segment.Index = Convert.ToInt32(arrayAfter.Groups[2].Value);
            }
            return segment;
        }
        #endregion

        #region Public Methods
        public void AppendPath(string relativePath)
        {
            FullPath += relativePath;
            ReadPath();
        }

        public void RemovePath(string relativePath)
        {
            FullPath = FullPath.Replace(relativePath, "");
            ReadPath();
        }

        public string GetPrePath()
        {
            var builder = new JsonPathBuilder(this.ToString());
            builder.Segments[1].Index = builder.Segments[1].Index - 1;
            return builder.ToString();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var segment in Segments)
            {
                sb.Append(segment.Name);
                if (segment.Index > -1)
                    sb.Append($"[{segment.Index}]");
                sb.Append(".");
            }
            return sb.ToString().TrimEnd('.');
        }
        #endregion
    }

    public class JsonPathSegment
    {
        #region Properties
        public string Name { get; set; }
        public int Index { get; set; } = -1;
        #endregion

        #region Public Methods
        public override string ToString()
        {
            return $"{Name} - {Index}";
        }
        #endregion
    }
    #endregion
}