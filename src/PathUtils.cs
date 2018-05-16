namespace Ser.ConAai
{
    #region Usings
    using NLog;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    #endregion

    public class PathUtils
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        public static string GetFullPathFromApp(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path))
                    return null;
                if (path.StartsWith("/"))
                    return path;
                if (!path.StartsWith("\\\\") && !path.Contains(":") && !path.StartsWith("%"))
                    path = Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), path.TrimStart('\\'));
                path = Environment.ExpandEnvironmentVariables(path);
                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }
    }
}
