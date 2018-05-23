using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ser.ConAai
{
    public static class JsonExtended
    {
        public static JToken RemoveFields(this JToken token, string[] fields)
        {
            if (!(token is JContainer container)) return token;
            List<JToken> removeList = new List<JToken>();
            foreach (JToken el in container.Children())
            {
                if (el is JProperty p && fields.Contains(p.Name))
                {
                    removeList.Add(el);
                }
                el.RemoveFields(fields);
            }

            foreach (JToken el in removeList)
            {
                el.Remove();
            }

            return token;
        }
    }
}
