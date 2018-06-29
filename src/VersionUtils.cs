#region License
/*
Copyright (c) 2018 Konrad Mattheis und Martin Berthold
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
#endregion

namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using Q2g.HelperPem;
    using Q2g.HelperQrs;
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text;
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