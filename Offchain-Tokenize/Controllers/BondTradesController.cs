using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;
using Offchain_Tokenize.Services;
using System.Globalization;

namespace Offchain_Tokenize.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BondTradesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ICreWorkflowClient _creWorkflowClient;
        private readonly ILogger<BondTradesController> _logger;

        public BondTradesController(
            AppDbContext context,
            ICreWorkflowClient creWorkflowClient,
            ILogger<BondTradesController> logger)
        {
            _context = context;
            _creWorkflowClient = creWorkflowClient;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<BondTrade>> CreateTrade([FromBody] CreateBondTradeRequest request)
        {
            var investorExists = await _context.Investors.AnyAsync(i => i.Id == request.InvestorId);
            if (!investorExists)
            {
                return BadRequest($"InvestorId {request.InvestorId} does not exist.");
            }

            var bondExists = await _context.BondInstances.AnyAsync(b => b.Id == request.BondInstanceId);
            if (!bondExists)
            {
                return BadRequest($"BondInstanceId {request.BondInstanceId} does not exist.");
            }

            if (request.AmountPaid < 0 || request.BondsReceived < 0)
            {
                return BadRequest("Amounts must be non-negative.");
            }

            var now = DateTime.UtcNow;
            var trade = new BondTrade
            {
                InvestorId = request.InvestorId,
                BondInstanceId = request.BondInstanceId,
                AmountPaid = request.AmountPaid,
                BondsReceived = request.BondsReceived,
                Status = string.IsNullOrWhiteSpace(request.Status)
                    ? BondTradeStatus.InProgress.ToString()
                    : request.Status.Trim(),
                Created = now,
                Modified = now
            };

            _context.BondTrades.Add(trade);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetTradeById), new { id = trade.Id }, trade);
        }

        [HttpPost("initiate")]
        public async Task<ActionResult<BondTrade>> InitiateTrade([FromBody] InitiateBondTradeRequest request, CancellationToken cancellationToken)
        {
            var investor = await _context.Investors.FirstOrDefaultAsync(i => i.Id == request.InvestorId, cancellationToken);
            if (investor is null)
            {
                return BadRequest($"InvestorId {request.InvestorId} does not exist.");
            }

            var bond = await _context.BondInstances.FirstOrDefaultAsync(b => b.Id == request.BondInstanceId, cancellationToken);
            if (bond is null)
            {
                return BadRequest($"BondInstanceId {request.BondInstanceId} does not exist.");
            }

            if (request.AmountPaid < 0 || request.BondsReceived <= 0)
            {
                return BadRequest("AmountPaid must be non-negative and BondsReceived must be greater than zero.");
            }

            var now = DateTime.UtcNow;
            var trade = new BondTrade
            {
                InvestorId = request.InvestorId,
                BondInstanceId = request.BondInstanceId,
                AmountPaid = request.AmountPaid,
                BondsReceived = request.BondsReceived,
                Status = BondTradeStatus.InProgress.ToString(),
                Created = now,
                Modified = now
            };

            _context.BondTrades.Add(trade);
            await _context.SaveChangesAsync(cancellationToken);

            var conversionRatio = 0L;
            if (!string.IsNullOrWhiteSpace(bond.ConversionRatio))
            {
                long.TryParse(bond.ConversionRatio, NumberStyles.Integer, CultureInfo.InvariantCulture, out conversionRatio);
            }

            var crePayload = new CreBondIssuanceRequest
            {
                IssuerId = string.IsNullOrWhiteSpace(bond.IssuerAddress)
                    ? $"bond-trade-{trade.Id}"
                    : bond.IssuerAddress,
                Isin = bond.ISIN,
                Currency = string.IsNullOrWhiteSpace(bond.Currency) ? "USD" : bond.Currency,
                TotalSize = request.BondsReceived.ToString(CultureInfo.InvariantCulture),
                FaceValue = bond.FaceValue.ToString(CultureInfo.InvariantCulture),
                MaturityDate = new DateTimeOffset(bond.MaturityDate).ToUnixTimeSeconds(),
                ConversionRatio = conversionRatio,
                ConversionPrice = request.ConversionPrice
            };

            var creResult = await _creWorkflowClient.TriggerBondIssuanceAsync(crePayload, cancellationToken);
            trade.CreResponse = creResult.ResponseBody;

            if (!creResult.Success)
            {
                trade.Status = BondTradeStatus.Failed.ToString();
                trade.FailureReason = creResult.Error;
                trade.Modified = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogWarning(
                    "Failed to trigger CRE for trade {TradeId}. Error: {Error}",
                    trade.Id,
                    creResult.Error
                );

                return StatusCode(502, new
                {
                    error = "Failed to trigger CRE workflow",
                    tradeId = trade.Id,
                    details = creResult.Error
                });
            }

            trade.Modified = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("CRE workflow triggered successfully for trade {TradeId}", trade.Id);
            return CreatedAtAction(nameof(GetTradeById), new { id = trade.Id }, trade);
        }

        [HttpPatch("{id:int}/status")]
        public async Task<ActionResult<BondTrade>> UpdateTradeStatus(int id, [FromBody] UpdateBondTradeStatusRequest request)
        {
            var trade = await _context.BondTrades.FirstOrDefaultAsync(t => t.Id == id);
            if (trade is null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Status))
            {
                return BadRequest("Status is required.");
            }

            trade.Status = request.Status.Trim();
            trade.Modified = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(trade);
        }

        /// <summary>
        /// CRE callback endpoint used by workflows after on-chain issuance processing.
        /// Expected lifecycle:
        /// InProgress -> Completed | Failed
        /// </summary>
        [HttpPost("cre-callback")]
        public async Task<ActionResult<BondTrade>> HandleCreCallback(
            [FromBody] BondTradeCreCallbackRequest request,
            CancellationToken cancellationToken)
        {
            var trade = await _context.BondTrades.FirstOrDefaultAsync(t => t.Id == request.TradeId, cancellationToken);
            if (trade is null)
            {
                return NotFound($"BondTrade {request.TradeId} was not found.");
            }

            if (string.IsNullOrWhiteSpace(request.Status))
            {
                return BadRequest("Status is required.");
            }

            trade.Status = request.Status.Trim();
            trade.OnChainTxHash = request.TransactionHash;
            trade.OnChainBlockNumber = request.BlockNumber;
            trade.OnChainBondId = request.BondId;
            trade.OnChainEquityId = request.EquityId;
            trade.FailureReason = request.Error;
            trade.Modified = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "CRE callback updated trade {TradeId} to status {Status}. TxHash: {TxHash}",
                trade.Id,
                trade.Status,
                trade.OnChainTxHash
            );

            return Ok(trade);
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<BondTrade>>> GetTrades()
        {
            var trades = await _context.BondTrades
                .AsNoTracking()
                .OrderByDescending(t => t.Id)
                .ToListAsync();

            return Ok(trades);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<BondTrade>> GetTradeById(int id)
        {
            var trade = await _context.BondTrades
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id);

            if (trade is null)
            {
                return NotFound();
            }

            return Ok(trade);
        }

        public sealed class CreateBondTradeRequest
        {
            public int InvestorId { get; set; }
            public int BondInstanceId { get; set; }
            public decimal AmountPaid { get; set; }
            public decimal BondsReceived { get; set; }
            public string? Status { get; set; }
        }

        public sealed class UpdateBondTradeStatusRequest
        {
            public string Status { get; set; } = string.Empty;
        }

        public sealed class InitiateBondTradeRequest
        {
            public int InvestorId { get; set; }
            public int BondInstanceId { get; set; }
            public decimal AmountPaid { get; set; }
            public decimal BondsReceived { get; set; }
            public long ConversionPrice { get; set; }
        }

        public sealed class BondTradeCreCallbackRequest
        {
            public int TradeId { get; set; }
            public string Status { get; set; } = string.Empty;
            public string? TransactionHash { get; set; }
            public long? BlockNumber { get; set; }
            public long? BondId { get; set; }
            public long? EquityId { get; set; }
            public string? Error { get; set; }
        }
    }
}
