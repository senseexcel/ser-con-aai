namespace Ser.ConAai.Functions
{
    #region Usings
    using Ser.ConAai.Config;
    using System;
    using Q2g.HelperPem;
    using Ser.ConAai.Communication;
    using NLog;
    using Q2g.HelperQlik;
    #endregion

    public class EncryptFunction: BaseFunction
    {
        #region Logger
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Constructor
        public EncryptFunction(RuntimeOptions options) : base(options) { }
        #endregion

        #region Public Methods
        public QlikResponse GetEncryptedText(QlikRequest request)
        {
            var result = new QlikResponse();

            try
            {
                var privateKeyPath = HelperUtilities.GetFullPathFromApp(Options.Config.Connection.Credentials.PrivateKey);
                var crypter = new AesCrypter(privateKeyPath);
                return new QlikResponse()
                {
                    EncryptText = crypter.EncryptText(request.EncryptText)
                };
            }
            catch (Exception ex)
            {
                result.SetErrorMessage(ex);
                logger.Error(ex, "The encrypt function has an unknown error.");
                return null;
            }
        }
        #endregion
    }
}