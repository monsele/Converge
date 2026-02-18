using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;

namespace Offchain_Tokenize.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EquityInstancesController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public EquityInstancesController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpPost]
        public async Task<ActionResult<EquityInstance>> CreateEquityInstance([FromBody] CreateEquityInstanceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Equity name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Symbol))
            {
                return BadRequest("Equity symbol is required.");
            }

            var bondExists = await _dbContext.BondInstances
                .AsNoTracking()
                .AnyAsync(b => b.Id == request.BondId);

            if (!bondExists)
            {
                return BadRequest("A valid BondId is required.");
            }

            var now = DateTime.UtcNow;
            var equityInstance = new EquityInstance
            {
                Name = request.Name.Trim(),
                Symbol = request.Symbol.Trim(),
                BondId = request.BondId,
                Created = now,
                Modified = now
            };

            _dbContext.EquityInstances.Add(equityInstance);
            await _dbContext.SaveChangesAsync();

            return CreatedAtAction(nameof(GetEquityInstanceById), new { id = equityInstance.Id }, equityInstance);
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<EquityInstance>>> GetEquityInstances()
        {
            var equityInstances = await _dbContext.EquityInstances
                .AsNoTracking()
                .Include(e => e.BondInstance)
                .OrderBy(e => e.Id)
                .ToListAsync();

            return Ok(equityInstances);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<EquityInstance>> GetEquityInstanceById(int id)
        {
            var equityInstance = await _dbContext.EquityInstances
                .AsNoTracking()
                .Include(e => e.BondInstance)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (equityInstance is null)
            {
                return NotFound();
            }

            return Ok(equityInstance);
        }

        public sealed class CreateEquityInstanceRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public int BondId { get; set; }
        }
    }
}
