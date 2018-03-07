#region License
/*
Copyright (c) 2018 Konrad Mattheis und Martin Berthold
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
#endregion

namespace SerConAai
{
    #region Usings
    using Microsoft.Extensions.PlatformAbstractions;
    using NLog;
    using NLog.Config;
    using System;
    using System.IO;
    using System.Linq;
    #endregion

    class Program
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        static void Main(string[] args)
        {
            try
            {
                SetLoggerSettings("App.config");
                var service = new SSEtoSER();
                service.Start(args);
                Console.ReadKey();
                service.Stop();
                Console.WriteLine("Shutdown Service...");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error:\n{ex.Message}");
                logger.Error(ex);
                Environment.Exit(1);
            }
        }

        private static void SetLoggerSettings(string configName)
        {
            var path = Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, configName);
            if (!File.Exists(path))
            {
                var root = new FileInfo(path).Directory?.Parent?.Parent?.Parent;
                var files = root.GetFiles("App.config", SearchOption.AllDirectories).ToList();
                path = files.FirstOrDefault()?.FullName;
            }

            logger.Factory.Configuration = new XmlLoggingConfiguration(path, false);
        }
    }
}