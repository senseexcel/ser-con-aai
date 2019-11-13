namespace Ser.ConAai
{
    using Newtonsoft.Json;
    #region Usings
    using Q2g.HelperPem;
    using Q2g.HelperQlik;
    using Q2g.HelperQrs;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    #endregion

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
                   typeof(TextCrypter).Assembly,
                   typeof(QlikQrsHub).Assembly,
                   typeof(ConnectionManager).Assembly,
                   typeof(Distribute.Distribute).Assembly,
                   Assembly.LoadFile(enginePath),
                   typeof(Ser.Engine.Rest.SerController).Assembly,
                   typeof(SerEvaluator).Assembly
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

        public static string GetExternalPackageJson()
        {
            try
            {
                var fullpath = Path.Combine(AppContext.BaseDirectory, "serconaai_packages.json");
                if (File.Exists(fullpath))
                {
                    var json = File.ReadAllText(fullpath);
                    var obj = JsonConvert.DeserializeObject(json);
                    return JsonConvert.SerializeObject(obj);
                }
                return "[]";
            }
            catch (Exception ex)
            {
                throw new Exception("Can´t read external packages.", ex);
            }
        }
    }
}