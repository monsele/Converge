namespace Offchain_Tokenize.Models
{
    public class Investors : BaseEntity
    {
        #region Organizational Identity
        public string LegalEntityName { get; set; }
        public string Jurisdiction { get; set; }
        public string ArticlesOfIncorporation { get; set; }
        #endregion

        #region Legal & Trust Setup
        public string TrusteeEntityName { get; set; }
        public string TrustIndentureRepo { get; set; }
        #endregion
    }
}
