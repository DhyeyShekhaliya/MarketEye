using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceAndScreeningTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CorporateActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AdjustmentFactor = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    DividendAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NewTicker = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RawDescription = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorporateActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CorporateActions_Securities_SecurityId",
                        column: x => x.SecurityId,
                        principalTable: "Securities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FundamentalRatios",
                columns: table => new
                {
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    ReportedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Pe = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Pb = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Ps = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Roe = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Roic = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DebtToEquity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    GrossMargin = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    FcfYield = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    MarketCap = table.Column<decimal>(type: "decimal(20,2)", precision: 20, scale: 2, nullable: true),
                    Basis = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundamentalRatios", x => new { x.SecurityId, x.ReportedDate });
                    table.ForeignKey(
                        name: "FK_FundamentalRatios_Securities_SecurityId",
                        column: x => x.SecurityId,
                        principalTable: "Securities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Indicators",
                columns: table => new
                {
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Sma50 = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Sma200 = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Rsi14 = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Macd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    MacdSignal = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Atr14 = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Vol30 = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Indicators", x => new { x.SecurityId, x.Date });
                    table.ForeignKey(
                        name: "FK_Indicators_Securities_SecurityId",
                        column: x => x.SecurityId,
                        principalTable: "Securities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricConcepts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ColumnName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AllowedOperatorsCsv = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MinValue = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    MaxValue = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultThreshold = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DefaultOperator = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceBars",
                columns: table => new
                {
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AdjClose = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    IsCircuitLocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceBars", x => new { x.SecurityId, x.Date });
                    table.ForeignKey(
                        name: "FK_PriceBars_Securities_SecurityId",
                        column: x => x.SecurityId,
                        principalTable: "Securities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScreenRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotId = table.Column<int>(type: "int", nullable: false),
                    CriteriaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResultCount = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScreenRuns_DataSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "DataSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CorporateActions_SecurityId_EffectiveDate_ActionType",
                table: "CorporateActions",
                columns: new[] { "SecurityId", "EffectiveDate", "ActionType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FundamentalRatios_ReportedDate",
                table: "FundamentalRatios",
                column: "ReportedDate");

            migrationBuilder.CreateIndex(
                name: "IX_Indicators_Date",
                table: "Indicators",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MetricConcepts_Name",
                table: "MetricConcepts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceBars_Date",
                table: "PriceBars",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_ScreenRuns_RunAt",
                table: "ScreenRuns",
                column: "RunAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScreenRuns_SnapshotId",
                table: "ScreenRuns",
                column: "SnapshotId");

            // --- §4.2: clustered columnstore on the two analytical tables ---
            //
            // EF Core cannot express CLUSTERED COLUMNSTORE, so this drops to raw SQL. Both tables
            // are wide, append-only and scanned rather than seeked, which is the access pattern
            // columnstore exists for.
            //
            // The existing clustered PK must become NONCLUSTERED first: a table can have only one
            // clustered index, and the columnstore is now it.
            //
            // §4.2 is explicit that the benefit is a HYPOTHESIS until benchmarked. Do not quote a
            // speedup figure anywhere until §9's suite has been run and recorded.
            migrationBuilder.Sql(@"
                ALTER TABLE dbo.PriceBars DROP CONSTRAINT PK_PriceBars;
                ALTER TABLE dbo.PriceBars ADD CONSTRAINT PK_PriceBars
                    PRIMARY KEY NONCLUSTERED (SecurityId, Date);
                CREATE CLUSTERED COLUMNSTORE INDEX CCI_PriceBars ON dbo.PriceBars;");

            migrationBuilder.Sql(@"
                ALTER TABLE dbo.Indicators DROP CONSTRAINT PK_Indicators;
                ALTER TABLE dbo.Indicators ADD CONSTRAINT PK_Indicators
                    PRIMARY KEY NONCLUSTERED (SecurityId, Date);
                CREATE CLUSTERED COLUMNSTORE INDEX CCI_Indicators ON dbo.Indicators;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS CCI_PriceBars ON dbo.PriceBars;
                DROP INDEX IF EXISTS CCI_Indicators ON dbo.Indicators;");

            migrationBuilder.DropTable(
                name: "CorporateActions");

            migrationBuilder.DropTable(
                name: "FundamentalRatios");

            migrationBuilder.DropTable(
                name: "Indicators");

            migrationBuilder.DropTable(
                name: "MetricConcepts");

            migrationBuilder.DropTable(
                name: "PriceBars");

            migrationBuilder.DropTable(
                name: "ScreenRuns");
        }
    }
}
