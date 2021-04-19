namespace Ser.ConAai.Functions
{
    #region Usings
    using System;
    using Ser.ConAai.Config;
    using Ser.Diagnostics;
    #endregion

    public class BaseFunction
    {
        #region Properties
        public RuntimeOptions Options { get; set; }
        #endregion

        #region Constructor
        public BaseFunction(RuntimeOptions options)
        {
            Options = options;
        }
        #endregion
    }
}