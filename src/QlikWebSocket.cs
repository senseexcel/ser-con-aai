namespace SerConAai
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using WebSocket4Net;
    #endregion

    public class QlikWebSocket
    {
        private WebSocket websocket;
        private string response;
        private Exception error;
        private bool isOpen;
        private bool isClose;

        public QlikWebSocket(string host, Cookie cookie)
        {
            var url = $"ws://{host}/app/engineData";
            var cookies = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>(cookie.Name, cookie.Value), };
            websocket = new WebSocket(url, cookies: cookies, version: WebSocketVersion.Rfc6455);
            websocket.Opened += Websocket_Opened;
            websocket.Error += Websocket_Error;
            websocket.Closed += Websocket_Closed;
            websocket.MessageReceived += Websocket_MessageReceived;
            websocket.AutoSendPingInterval = 100;
            websocket.EnableAutoSendPing = true;
            //websocket.Security.AllowCertificateChainErrors = true;
            //websocket.Security.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls;
        }

        private void Websocket_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            response = e.Message;
        }

        private void Websocket_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            error = e.Exception;
        }

        private void Websocket_Closed(object sender, EventArgs e)
        {
            isClose = true;
        }

        private void Websocket_Opened(object sender, EventArgs e)
        {
            isOpen = true;
        }

        private JObject Send(string json)
        {
            var msg = json.Replace("'", "\"");

            try
            {
                response = null;
                error = null;
                websocket.Send(msg);

                while (error == null && response == null)
                    Thread.Sleep(250);

                if (error != null)
                {
                    throw error;
                }
                return JObject.Parse(response);
            }
            catch (Exception ex)
            {
                var fullEx = new Exception($"Request {msg} could not send.", ex);
                return JObject.Parse(JsonConvert.SerializeObject(ex));
            }
        }

        public bool OpenSocket()
        {
            try
            {
                if (isOpen == true)
                    return true;
                websocket.Open();
                while (!isOpen && error == null)
                    Thread.Sleep(250);
                if (error != null)
                    throw error;
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception("Socket could not open.", ex);
            }
        }

        public bool CloseSocket()
        {
            try
            {
                if (isClose == true)
                    return true;
                websocket.Close();
                while (!isClose && error == null)
                    Thread.Sleep(250);
                if (error != null)
                    throw error;
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception("Socket could not close.", ex);
            }
        }

        public JObject OpenDoc(string appId)
        {
            var msg = @"{'method':'OpenDoc','handle':-1,'params':{'qDocName':'" + appId + "', 'qNoData':false},'jsonrpc':'2.0'}";
            return Send(msg);
        }

        public JObject GetContentLibraries(string handle)
        {
            var msg = @"{'method':'GetContentLibraries','handle':" + handle + ",'params':{},'jsonrpc':'2.0'}";
            return Send(msg);
        }

        public JObject GetLibraryContent(string handle, string qName)
        {
            var msg = @"{'method':'GetLibraryContent','handle':" + handle + ",'params':{'qName':'" + qName + "'},'jsonrpc':'2.0'}";
            return Send(msg);
        }

        public JObject IsDesktop()
        {
            var msg = @"{'method':'OpenDoc','handle':-1,'params':{'qDocName':'ee9799d9-55b0-4225-99cb-b3d5ddf7a9d6', 'qNoData':false},'jsonrpc':'2.0'}";
            return Send(msg);
        }
    }
}