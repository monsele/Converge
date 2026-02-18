namespace Offchain_Tokenize.Models
{
    public class EquityInstance : BaseEntity
    {
        
        public string Name { get; set; }
        public BondInstance BondInstance { get; set; }
        public int BondId { get; set; }
        public string Symbol { get; set; }
    }
}