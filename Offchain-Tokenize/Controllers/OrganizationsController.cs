using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;

namespace Offchain_Tokenize.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrganizationsController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public OrganizationsController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpPost]
        public async Task<ActionResult<Organization>> CreateOrganization([FromBody] CreateOrganizationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Organization name is required.");
            }

            var organization = new Organization
            {
                Name = request.Name.Trim(),
                Jurisdiction = request.Jurisdiction?.Trim() ?? string.Empty,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow
            };

            _dbContext.Organizations.Add(organization);
            await _dbContext.SaveChangesAsync();

            return CreatedAtAction(nameof(GetOrganizationById), new { id = organization.Id }, organization);
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Organization>>> GetOrganizations()
        {
            var organizations = await _dbContext.Organizations
                .AsNoTracking()
                .OrderBy(o => o.Id)
                .ToListAsync();

            return Ok(organizations);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<Organization>> GetOrganizationById(int id)
        {
            var organization = await _dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == id);

            if (organization is null)
            {
                return NotFound();
            }

            return Ok(organization);
        }

        public sealed class CreateOrganizationRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Jurisdiction { get; set; }
        }
    }
}
