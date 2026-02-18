namespace Offchain_Tokenize.Models
{
    public class BondInstance : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string ISIN { get; set; } = string.Empty;
        public decimal FaceValue { get; set; }
        public decimal InterestRate { get; set; }
        public DateTime MaturityDate { get; set; }
        public string IssuerAddress { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public DateTime IssuanceDate { get; set; }
        public string ConversionRatio { get; set; }
    }
}
