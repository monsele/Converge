using Microsoft.AspNetCore.Mvc;
using Offchain_Tokenize.Models;

namespace Offchain_Tokenize.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BondConvertController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<BondConvertController> _logger;

    public BondConvertController(AppDbContext context, ILogger<BondConvertController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// API Endpoint: api/createBondConvert
    /// Receives ConvertibleBondCreated event data from Chainlink CRE workflow
    /// </summary>
    [HttpPost("createBondConvert")]
    public async Task<IActionResult> CreateBondConvert([FromBody] BondConvertRequest request)
    {
        try
        {
            _logger.LogInformation(
                "Received ConvertibleBondCreated event - BondId: {BondId}, EquityId: {EquityId}, Name: {Name}",
                request.BondId, request.EquityId, request.Name
            );

            // Validate required fields
            if (string.IsNullOrEmpty(request.Name))
                return BadRequest(new { error = "Name is required" });
            
            if (string.IsNullOrEmpty(request.Symbol))
                return BadRequest(new { error = "Symbol is required" });

            // Convert face value from string to decimal
            if (!decimal.TryParse(request.FaceValue, out var faceValue))
            {
                faceValue = 0;
            }

            // Create BondInstance record matching the existing model
            var bondInstance = new BondInstance
            {
                Name = request.Name,
                Symbol = request.Symbol,
                ISIN = request.Isin,
                FaceValue = faceValue,
                InterestRate = decimal.TryParse(request.CouponRate, out var rate) ? rate / 100 : 0, // Convert basis points to percentage
                MaturityDate = DateTimeOffset.FromUnixTimeSeconds((long)request.MaturityDate).DateTime,
                IssuerAddress = "0x0000000000000000000000000000000000000000", // Default or from event
                Status = "Active",
                Currency = "USD", // Default currency
                IssuanceDate = DateTime.UtcNow,
                ConversionRatio = request.ConversionRatio,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow
            };

            _context.BondInstances.Add(bondInstance);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Successfully created BondConvert record - BondId: {BondId}, EquityId: {EquityId}",
                request.BondId, request.EquityId
            );

            return Ok(new 
            { 
                success = true, 
                message = "BondConvert created successfully",
                data = new 
                {
                    bondId = request.BondId,
                    equityId = request.EquityId,
                    name = request.Name,
                    symbol = request.Symbol,
                    isin = request.Isin
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ConvertibleBondCreated event");
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint for CRE workflow
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}

/// <summary>
/// Request model for ConvertibleBondCreated event data
/// </summary>
public class BondConvertRequest
{
    /// <summary>
    /// Unique identifier for the bond series
    /// </summary>
    public long BondId { get; set; }

    /// <summary>
    /// Unique identifier for the equity class
    /// </summary>
    public long EquityId { get; set; }

    /// <summary>
    /// Conversion ratio for bond to equity conversion (in wei)
    /// </summary>
    public string ConversionRatio { get; set; } = string.Empty;

    /// <summary>
    /// Unix timestamp for bond maturity date
    /// </summary>
    public ulong MaturityDate { get; set; }

    /// <summary>
    /// Face value of each bond (in wei)
    /// </summary>
    public string FaceValue { get; set; } = string.Empty;

    /// <summary>
    /// Trading symbol for the bond
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Full name of the bond
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// International Securities Identification Number
    /// </summary>
    public string Isin { get; set; } = string.Empty;

    /// <summary>
    /// Coupon rate in basis points (e.g., 500 = 5%)
    /// </summary>
    public string CouponRate { get; set; } = string.Empty;

    /// <summary>
    /// Transaction hash from the blockchain event
    /// </summary>
    public string? TransactionHash { get; set; }

    /// <summary>
    /// Block number where the event was emitted
    /// </summary>
    public int? BlockNumber { get; set; }

    /// <summary>
    /// Timestamp when the event was processed
    /// </summary>
    public long? Timestamp { get; set; }
}
