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
    using Ser.Api;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    #endregion

    public class UserParameter
    {
        #region Properties
        public string TemplateFileName { get; set; }
        public string SaveFormats { get; set; }
        public SelectionMode UseUserSelesction { get; set; }
        public string AppId { get; set; }
        public DomainUser DomainUser { get; set; }
        public Cookie ConnectCookie { get; set; }
        public bool SharedMode { get; set; }
        public string BookmarkId { get; set; }
        public bool OnDemand { get; set; }
        public string PrivateKeyPath { get; set; }
        #endregion
    }

    public class OnDemandResult
    {
        #region Properties
        public int Status { get; set; }
        public string TaskId { get; set; }
        public string Link { get; set; }
        public string Log { get; set; }
        public string Version { get; set; }
        #endregion
    }
}
