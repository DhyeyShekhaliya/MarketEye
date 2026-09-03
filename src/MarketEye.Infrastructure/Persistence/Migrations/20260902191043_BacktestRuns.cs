using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BacktestRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    FinalEquity = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    CagrGross = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CagrNet = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    MaxDrawdown = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Sharpe = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Sortino = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    WinRate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    AnnualTurnover = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    TotalCostsPaid = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    BenchmarkTicker = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BenchmarkCagr = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    EquityCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BenchmarkCurveJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkPrices",
                columns: table => new
                {
                    Ticker = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalReturnIndexValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkPrices", x => new { x.Ticker, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "BacktestRebalances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BacktestRunId = table.Column<long>(type: "bigint", nullable: false),
                    SignalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExecutionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CashAfter = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    PortfolioValueAfter = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    CostsPaid = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: false),
                    TurnoverPct = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    HoldingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRebalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacktestRebalances_BacktestRuns_BacktestRunId",
                        column: x => x.BacktestRunId,
                        principalTable: "BacktestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRebalances_BacktestRunId_SignalDate",
                table: "BacktestRebalances",
                columns: new[] { "BacktestRunId", "SignalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_RunAt",
                table: "BacktestRuns",
                column: "RunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestRebalances");

            migrationBuilder.DropTable(
                name: "BenchmarkPrices");

            migrationBuilder.DropTable(
                name: "BacktestRuns");
        }
    }
}
