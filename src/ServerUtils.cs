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
    using NLog;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text;
    #endregion

    public class ServerUtils
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        public static string GetFullQualifiedHostname(int timeout)
        {
            try
            {
                var serverName = Environment.MachineName;
                var result = Dns.BeginGetHostEntry(serverName, null, null);
                if (result.AsyncWaitHandle.WaitOne(timeout, true))
                    return Dns.EndGetHostEntry(result).HostName;
                else
                    return Environment.MachineName;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return Environment.MachineName;
            }
        }

        public static IPAddress GetServerIp(int timeout)
        {
            try
            {
                var hostName = GetFullQualifiedHostname(timeout);
                var result = Dns.GetHostEntry(hostName).AddressList.FirstOrDefault(a =>
                           a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return result;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }
    }
}
