using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Models;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Offchain_Tokenize.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
        {
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Investors> Investors { get; set; }
        public DbSet<BondInstance> BondInstances { get; set; }
        public DbSet<EquityInstance> EquityInstances { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Add any custom configuration here if needed
        }
    }
}

