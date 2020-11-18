namespace Ser.ConAai
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Text;
    #endregion

    public class TaskStatusRequest
    {
        #region Properties
        public string ManagedTaskId { get; set; }
        public string TaskMode { get; set; }
        public string VersionMode { get; set; }
        #endregion
    }
}