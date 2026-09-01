using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundamentalsSharesAndCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CostOfRevenue",
                table: "Fundamentals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SharesOutstanding",
                table: "Fundamentals",
                type: "decimal(24,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostOfRevenue",
                table: "Fundamentals");

            migrationBuilder.DropColumn(
                name: "SharesOutstanding",
                table: "Fundamentals");
        }
    }
}
