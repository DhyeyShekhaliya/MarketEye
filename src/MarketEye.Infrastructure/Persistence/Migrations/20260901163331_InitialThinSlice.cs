using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
/// <summary>
    /// Phase 0 thin slice: Securities, DataSnapshots, IngestionRuns, and Fundamentals as a
    /// system-versioned temporal table (PLAN.md §4.1).
    ///
    /// NOT here, deliberately: PriceBars, Indicators, FundamentalRatios, MetricConcepts,
    /// Strategies, ScreenRuns, BacktestRuns, ParseCache, ScreenResultCache. Those are
    /// designed in Phase 1 alongside the code that uses them.
    ///
    /// NOTE FOR PHASE 1: §4.2 requires a CLUSTERED COLUMNSTORE INDEX on PriceBars and
    /// Indicators. EF Core cannot express clustered columnstore, so that migration must
    /// drop to migrationBuilder.Sql("CREATE CLUSTERED COLUMNSTORE INDEX ..."). §4.2 also
    /// requires benchmarking rowstore vs columnstore rather than assuming the win.
    /// </summary>
    public partial class InitialThinSlice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SealedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PriceRowCount = table.Column<long>(type: "bigint", nullable: false),
                    FundamentalRowCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Securities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Ticker = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderSecurityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Sector = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DelistedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DelistingReason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Securities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RecordsWritten = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SnapshotId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionRuns_DataSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "DataSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Fundamentals",
                columns: table => new
                {
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    FiscalPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    ReportedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Revenue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetIncome = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TotalDebt = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ShareholdersEquity = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fundamentals", x => new { x.SecurityId, x.FiscalPeriodEnd });
                    table.ForeignKey(
                        name: "FK_Fundamentals_Securities_SecurityId",
                        column: x => x.SecurityId,
                        principalTable: "Securities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "FundamentalsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_DataSnapshots_AsOfDate_SealedAt",
                table: "DataSnapshots",
                columns: new[] { "AsOfDate", "SealedAt" },
                filter: "[SealedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Fundamentals_ReportedDate",
                table: "Fundamentals",
                column: "ReportedDate");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_SnapshotId",
                table: "IngestionRuns",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_Securities_ProviderSecurityId",
                table: "Securities",
                column: "ProviderSecurityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Securities_Ticker",
                table: "Securities",
                column: "Ticker",
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Fundamentals")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "FundamentalsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "IngestionRuns");

            migrationBuilder.DropTable(
                name: "Securities");

            migrationBuilder.DropTable(
                name: "DataSnapshots");
        }
    }
}
