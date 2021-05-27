namespace Ser.ConAai.Config
{
    #region Usings
    using System;
    using System.IO;
    using System.Reflection;
    using Newtonsoft.Json;
    #endregion

    public class ConnectorVersion
    {
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

        public static string GetMainVersion()
        {
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (appVersion == null)
                return "unknown";
            return $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}";
        }
    }
}