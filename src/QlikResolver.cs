namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json.Linq;
    using NLog;
    using Qlik.EngineAPI;
    using System;
    using System.Collections.Generic;
    using System.Text;
    #endregion

    public class QlikResolver
    {
        #region Variables & Properties
        private IDoc App { get; set; }
        private JObject Json { get; set; }
        #endregion

        #region Constructor
        public QlikResolver(IDoc qlikApp)
        {
            App = qlikApp ?? throw new Exception("QlikSense-App is null.");
        }
        #endregion

        #region Methods
        private string ResolveVariable(string value)
        {
            return value.Replace("$@(", "$(");
        }

        private void ResolveJTokenInternal(JToken jtoken)
        {
            if (jtoken.HasValues)
            {
                ResolveInternal(jtoken.Children());
            }
            else
            {
                if (jtoken.Type == JTokenType.Object)
                    return;
                var value = jtoken?.ToObject<string>() ?? String.Empty;
                if (value.StartsWith("="))
                {
                    var parent = jtoken.Parent;
                    if (parent.Type == JTokenType.Property)
                    {
                        value = ResolveVariable(value);
                        var jProp = parent.Value<JProperty>();
                        jProp.Value = value;
                    }
                    else if (jtoken.Type == JTokenType.String)
                    {
                        value = ResolveVariable(value);
                        var jValue = jtoken.Value<JValue>();
                        jValue.Value = value;
                    }
                }
            }
        }

        private void ResolveInternal(JEnumerable<JToken> jtokens)
        {
            foreach (var jtoken in jtokens.Children())
            {
                if (jtoken is JArray)
                {
                    var array = jtoken as JArray;
                    foreach (var item in array)
                    {
                        var currentToken = item;
                        ResolveJTokenInternal(currentToken);
                    }
                }
                else
                {
                    var currentToken = jtoken;
                    ResolveJTokenInternal(currentToken);
                }
            }
        }

        public JObject Resolve(JObject value)
        {
            try
            {
                if (value == null)
                    return null;

                Json = new JObject(value);
                ResolveInternal(Json.Children());
                return Json;
            }
            catch (Exception ex)
            {
                throw new Exception($"The evaluate has an error. {value.ToString()}", ex);
            }
        }
        #endregion
    }
}
