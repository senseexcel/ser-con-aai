namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.IO;
    using Newtonsoft.Json;
    #endregion

    public class VersionUtils
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
            //Get package versions from json file (msbuild)
            return "4.6.1";
        }
    }
}