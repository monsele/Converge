using System.ComponentModel.DataAnnotations.Schema;

namespace Offchain_Tokenize.Models
{
    public class BondTrade : BaseEntity
    {
        public int InvestorId { get; set; }
        public Investors? Investor { get; set; }

        public int BondInstanceId { get; set; }
        public BondInstance? BondInstance { get; set; }

        public decimal AmountPaid { get; set; }
        public decimal BondsReceived { get; set; }

        public string Status { get; set; } = BondTradeStatus.InProgress.ToString();
        public string? FailureReason { get; set; }
        public string? CreResponse { get; set; }
        public string? OnChainTxHash { get; set; }
        public long? OnChainBlockNumber { get; set; }
        public long? OnChainBondId { get; set; }
        public long? OnChainEquityId { get; set; }

        [NotMapped]
        public BondTradeStatus TradeStatus
        {
            get => Enum.TryParse<BondTradeStatus>(Status, true, out var parsed)
                ? parsed
                : BondTradeStatus.InProgress;
            set => Status = value.ToString();
        }
    }
}
