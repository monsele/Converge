using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;

namespace Offchain_Tokenize.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class InvestorsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public InvestorsController(AppDbContext context)
        {
            _context = context;
        }

        // GET: api/Investors
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Investors>>> GetInvestors()
        {
            return await _context.Investors.ToListAsync();
        }

        // GET: api/Investors/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Investors>> GetInvestor(int id)
        {
            var investor = await _context.Investors.FindAsync(id);

            if (investor == null)
            {
                return NotFound();
            }

            return investor;
        }

        // POST: api/Investors
        [HttpPost]
        public async Task<ActionResult<Investors>> CreateInvestor(Investors investor)
        {
            investor.Created = DateTime.UtcNow;
            investor.Modified = DateTime.UtcNow;

            _context.Investors.Add(investor);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetInvestor), new { id = investor.Id }, investor);
        }
    }
}
 