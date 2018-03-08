namespace SerConAai
{
    #region Usings
    using SerApi;
    using System;
    using System.Collections.Generic;
    using System.Text;
    #endregion

    public class UserParameter
    {
        public string TemplateFileName { get; set; }
        public string SaveFormats { get; set; }
        public bool UseUserSelesction { get; set; }
        public string AppId { get; set; }
        public DomainUser DomainUser { get; set; }
    }
}
