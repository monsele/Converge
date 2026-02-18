using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;

namespace Offchain_Tokenize.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BondInstancesController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public BondInstancesController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpPost]
        public async Task<ActionResult<BondInstance>> CreateBondInstance([FromBody] CreateBondInstanceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Bond name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Symbol))
            {
                return BadRequest("Bond symbol is required.");
            }

            if (request.MaturityDate <= request.IssuanceDate)
            {
                return BadRequest("MaturityDate must be after IssuanceDate.");
            }

            var now = DateTime.UtcNow;
            var bondInstance = new BondInstance
            {
                Name = request.Name.Trim(),
                Symbol = request.Symbol.Trim(),
                ISIN = request.ISIN?.Trim() ?? string.Empty,
                FaceValue = request.FaceValue,
                InterestRate = request.InterestRate,
                MaturityDate = request.MaturityDate,
                IssuerAddress = request.IssuerAddress?.Trim() ?? string.Empty,
                Status = string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim(),
                Currency = request.Currency?.Trim() ?? string.Empty,
                IssuanceDate = request.IssuanceDate,
                Created = now,
                Modified = now
            };

            _dbContext.BondInstances.Add(bondInstance);
            await _dbContext.SaveChangesAsync();

            return CreatedAtAction(nameof(GetBondInstanceById), new { id = bondInstance.Id }, bondInstance);
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<BondInstance>>> GetBondInstances()
        {
            var bondInstances = await _dbContext.BondInstances
                .AsNoTracking()
                .OrderBy(b => b.Id)
                .ToListAsync();

            return Ok(bondInstances);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<BondInstance>> GetBondInstanceById(int id)
        {
            var bondInstance = await _dbContext.BondInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id);

            if (bondInstance is null)
            {
                return NotFound();
            }

            return Ok(bondInstance);
        }

        public sealed class CreateBondInstanceRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public string? ISIN { get; set; }
            public decimal FaceValue { get; set; }
            public decimal InterestRate { get; set; }
            public DateTime MaturityDate { get; set; }
            public string? IssuerAddress { get; set; }
            public string? Status { get; set; }
            public string? Currency { get; set; }
            public DateTime IssuanceDate { get; set; }
        }
    }
}
