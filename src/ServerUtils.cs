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
