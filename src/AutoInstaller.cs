namespace Ser.ConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using NLog;
    using Q2g.HelperQrs;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    #endregion

    public class AutoInstaller
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Enumerations
        public enum SecurityRuleType
        {
            CUSTOM,
            DEFAULT,
            READ_ONLY,
            ALL
        }
        #endregion

        #region Propertys
        private QlikQrsHub Hub = null;
        private List<string> WhiteList = null;
        #endregion

        public AutoInstaller(Uri connectUri, Cookie cookie)
        {
            Hub = new QlikQrsHub(connectUri, cookie);
            WhiteList = new List<string>()
            {
                "127.0.0.1",
                "localhost",
                $"{ServerUtils.GetFullQualifiedHostname(2000)}",
                $"{ServerUtils.GetServerIp(2000)}",
            };
        }

        private bool CheckQlikVersion()
        {
            try
            {
                var result = Hub.SendRequestAsync("about", HttpMethod.Get).Result;
                var version = JsonConvert.DeserializeObject<QlikVersion>(result);
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }

        private VirtualProxy GetVirtualProxy(QlikVirtualProxySettings settings, string headerName, string prefix, bool isDefaultProxy = false)
        {
            foreach (var proxy in settings.Settings.VirtualProxies)
                if (proxy.SessionCookieHeaderName == headerName && 
                    proxy.Prefix == prefix && 
                    proxy.DefaultVirtualProxy == isDefaultProxy)
                    return proxy;
            return null;
        }

        private VirtualProxy AddVirtualProxy(InstallParameter parameter, List<LoadBalancingServerNode> nodes)
        {
            try
            {
                if(!File.Exists(parameter.CertificatePath))
                    throw new Exception($"The certificate {parameter.CertificatePath} was not exists.");

                if(String.IsNullOrEmpty(parameter.CookieHeaderName))
                  throw new Exception("The cookie header name was empty.");
 
                if(nodes == null || nodes?.Count == 0)
                    throw new Exception("The load balancing server node is null.");

                var jwtCertificate = File.ReadAllText(parameter.CertificatePath).Trim();
                var proxyConfig = new VirtualProxy()
                {
                    Prefix = parameter.Prefix,
                    Description = parameter.Prefix,
                    UseStickyLoadBalancing = false,
                    LoadBalancingServerNodes = nodes,
                    AuthenticationMethod = 4,
                    HeaderAuthenticationMode = 0,
                    AnonymousAccessMode = 0,
                    WindowsAuthenticationEnabledDevicePattern = "Windows",
                    SessionCookieHeaderName = parameter.CookieHeaderName,
                    SessionInactivityTimeout = 30,
                    WebsocketCrossOriginWhiteList = WhiteList,
                    DefaultVirtualProxy = false,
                    JwtAttributeUserId = "UserId",
                    JwtAttributeUserDirectory = "UserDirectory",
                    JwtPublicKeyCertificate = jwtCertificate,
                };

                var json = JsonConvert.SerializeObject(proxyConfig, Formatting.Indented);
                var data = Encoding.UTF8.GetBytes(json);
                var content = new ContentData()
                {
                    ContentType = "application/json",
                    FileData = data,
                };

                var result = Hub.SendRequestAsync("virtualproxyconfig", HttpMethod.Post, content).Result;
                return JsonConvert.DeserializeObject<VirtualProxy>(result);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private bool CreateVirtualProxy(InstallParameter parameter)
        {
            try
            {
                //Get local proxys
                var results = Hub.SendRequestAsync("proxyservice/local", HttpMethod.Get).Result;
                var proxySettings = JsonConvert.DeserializeObject<QlikVirtualProxySettings>(results);
                if (GetVirtualProxy(proxySettings, parameter.CookieHeaderName, parameter.Prefix) != null)
                    throw new Exception($"The virtual proxy {parameter.CookieHeaderName} already exists.");

                var defaultProxy = GetVirtualProxy(proxySettings, "X-Qlik-Session", "", true);
                if (defaultProxy == null)
                    throw new Exception("No default virtual proxy found.");

                //create virtual proxy
                var result = AddVirtualProxy(parameter, defaultProxy?.LoadBalancingServerNodes ?? null);
                if (result == null)
                    throw new Exception("The virtual proxy for reporting could not be added.");

                logger.Debug($"The virtual proxy {result.Id} for reporting was successfull added.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }

        private bool CheckSecurityRules(SecurityRuleType type)
        {
            try
            {
                var filter = String.Empty;
                switch (type)
                {
                    case SecurityRuleType.CUSTOM:
                        filter = "Type eq 'Custom'";
                        break;
                    case SecurityRuleType.DEFAULT:
                        filter = "Type eq 'Default'";
                        break;
                    case SecurityRuleType.READ_ONLY:
                        filter = "Type eq 'Read only'";
                        break;
                    case SecurityRuleType.ALL:
                        break;
                    default:
                        throw new Exception($"The security rule filter {type.ToString()} is unknown.");
                }

                var result = Hub.SendRequestAsync("systemrule/full", HttpMethod.Get, null, filter).Result;
                if (result == null)
                    throw new Exception("No security rules found.");

                var securityRules = JsonConvert.DeserializeObject<List<QlikSecurityRule>>(result);
                if(securityRules.Count == 0)
                    throw new Exception("The count of security rules is 0.");

                logger.Debug($"The security rules was successfull found.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }

        private bool CheckAnalyticConnection(string name)
        {
            try
            {
                if (String.IsNullOrEmpty(name))
                    throw new Exception("The name of the analytic connection");

                var filter = $"Name eq '{name}'";
                var result = Hub.SendRequestAsync("analyticconnection/full", HttpMethod.Get, null, filter).Result;
                if (result == null)
                    throw new Exception($"No analytic connection with name {name} found.");

                var securityRules = JsonConvert.DeserializeObject<List<QlikAnalyticConnection>>(result);
                if (securityRules.Count == 0)
                    throw new Exception("The count of analytic connections is 0.");

                logger.Debug($"The analytic connection was successfull found.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }

        private bool ImportOnDemoExtention(string extensionPath)
        {
            try
            {
                if (!File.Exists(extensionPath))
                    throw new Exception($"The extention {extensionPath} was not found.");

                var extensionName = Path.GetFileNameWithoutExtension(extensionPath);
                var result = Hub.SendRequestAsync("extension/full", HttpMethod.Get, null, $"Name eq '{extensionName}'").Result;
                var extension = JsonConvert.DeserializeObject<List<QlikExtention>>(result)?.SingleOrDefault() ?? null;
                if (extension != null)
                    throw new Exception($"The extension {extensionName} already exists.");

                var data = File.ReadAllBytes(extensionPath);
                var zipContent = new ContentData()
                {
                    ContentType = "application/zip",
                    ExternalPath = extensionPath,
                    FileData = data,
                };

                result = Hub.SendRequestAsync("extension/upload", HttpMethod.Post, zipContent).Result;
                if (result == null)
                    throw new Exception("The extension for ondemand could not be imported.");

                extension = JsonConvert.DeserializeObject<List<QlikExtention>>(result).SingleOrDefault() ?? null;
                logger.Debug($"The extension {extension.Name} with id {extension.Id} was successfull imported.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }

        public bool Run(InstallParameter parameter)
        {
            try
            {
                //for special qlik version
                var result = CheckQlikVersion();

                result = CheckAnalyticConnection("ser");
                if (!result)
                    return false;

                result = CheckSecurityRules(SecurityRuleType.CUSTOM);
                if (!result)
                    return false;

                result = CreateVirtualProxy(parameter);
                if (!result)
                    return false;

                result = ImportOnDemoExtention(parameter.ExtentionPath);
                if (!result)
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return false;
            }
        }
    }

    public class InstallParameter
    {
        public string CookieHeaderName { get; set; }
        public string Prefix { get; set; }
        public string CertificatePath { get; set; }
        public string ExtentionPath { get; set; }
    }
}