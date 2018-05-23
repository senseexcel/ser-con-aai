using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Q2g.HelperPem;
using Q2g.HelperQrs;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Ser.ConAai
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class VersionInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }

        public override string ToString()
        {
            return Name;
        }
}

    public class VersionUtils
    {
        private static VersionInfo GetAssemblyVersion(Assembly assembly)
        {
            try
            {
                var type = typeof(AssemblyTitleAttribute);
                var title = assembly.GetCustomAttribute(type) as AssemblyTitleAttribute;
                type = typeof(AssemblyInformationalVersionAttribute);
                var gitVersion = assembly.GetCustomAttribute(type) as AssemblyInformationalVersionAttribute;

                return new VersionInfo()
                {
                    Name = title?.Title,
                    Version = gitVersion?.InformationalVersion,
                };
            }
            catch (Exception ex)
            {
                throw new Exception("Can´t read assembly version.", ex);
            }
        }

        public static List<VersionInfo> ReadAssemblyVersions(string enginePath)
        {
            try
            {
                var assemblys = new List<Assembly>()
                {
                   typeof(Distribute.Distribute).Assembly,
                   typeof(SerEvaluator).Assembly,
                   typeof(QlikQrsHub).Assembly,
                   typeof(TextCrypter).Assembly,
                   Assembly.LoadFile(enginePath),
                };

                var results = new List<VersionInfo>();
                foreach (var assembly in assemblys)
                    results.Add(GetAssemblyVersion(assembly));

                return results;
            }
            catch (Exception ex)
            {
                throw new Exception("Can´t read assembly version.", ex);
            }
        }
    }
}