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
        public DbSet<BondTrade> BondTrades { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<BondTrade>()
                .HasOne(t => t.Investor)
                .WithMany()
                .HasForeignKey(t => t.InvestorId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<BondTrade>()
                .HasOne(t => t.BondInstance)
                .WithMany()
                .HasForeignKey(t => t.BondInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}

