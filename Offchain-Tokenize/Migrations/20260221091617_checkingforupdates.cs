using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Offchain_Tokenize.Migrations
{
    /// <inheritdoc />
    public partial class checkingforupdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConversionRatio",
                table: "BondInstances",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "BondTrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvestorId = table.Column<int>(type: "integer", nullable: false),
                    BondInstanceId = table.Column<int>(type: "integer", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric", nullable: false),
                    BondsReceived = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreResponse = table.Column<string>(type: "text", nullable: true),
                    OnChainTxHash = table.Column<string>(type: "text", nullable: true),
                    OnChainBlockNumber = table.Column<long>(type: "bigint", nullable: true),
                    OnChainBondId = table.Column<long>(type: "bigint", nullable: true),
                    OnChainEquityId = table.Column<long>(type: "bigint", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BondTrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BondTrades_BondInstances_BondInstanceId",
                        column: x => x.BondInstanceId,
                        principalTable: "BondInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BondTrades_Investors_InvestorId",
                        column: x => x.InvestorId,
                        principalTable: "Investors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EquityInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BondInstanceId = table.Column<int>(type: "integer", nullable: false),
                    BondId = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EquityInstances_BondInstances_BondInstanceId",
                        column: x => x.BondInstanceId,
                        principalTable: "BondInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BondTrades_BondInstanceId",
                table: "BondTrades",
                column: "BondInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_BondTrades_InvestorId",
                table: "BondTrades",
                column: "InvestorId");

            migrationBuilder.CreateIndex(
                name: "IX_EquityInstances_BondInstanceId",
                table: "EquityInstances",
                column: "BondInstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BondTrades");

            migrationBuilder.DropTable(
                name: "EquityInstances");

            migrationBuilder.DropColumn(
                name: "ConversionRatio",
                table: "BondInstances");
        }
    }
}
