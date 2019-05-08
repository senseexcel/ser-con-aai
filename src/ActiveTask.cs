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
    using Qlik.EngineAPI;
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using enigma;
    using Newtonsoft.Json.Linq;
    using System.Threading;
    #endregion

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore,
                NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ActiveTask
    {
        public string Distribute { get; set; }
        public int Status { get; set; }
        public Guid Id { get; set; }
        public DateTime StartTime { get; set; }
        public SessionInfo Session { get; set; }
        public string Message { get; set; }
        public List<Guid> FileUploadIds { get; set; } = new List<Guid>();
        public JObject JobJson { get; set; }
        public bool Stoppable { get; set; }
        public bool Stopped { get; set; }
        public CancellationTokenSource CancelSource { get; set; }
    }
}