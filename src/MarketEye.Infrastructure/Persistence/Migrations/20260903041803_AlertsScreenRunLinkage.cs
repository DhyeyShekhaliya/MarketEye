using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertsScreenRunLinkage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MemberSecuritiesJson",
                table: "ScreenRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SavedStrategyId",
                table: "ScreenRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SavedStrategyId = table.Column<int>(type: "int", nullable: false),
                    SecurityId = table.Column<int>(type: "int", nullable: false),
                    Ticker = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ScreenRunId = table.Column<long>(type: "bigint", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertEvents_SavedStrategies_SavedStrategyId",
                        column: x => x.SavedStrategyId,
                        principalTable: "SavedStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertEvents_ScreenRuns_ScreenRunId",
                        column: x => x.ScreenRunId,
                        principalTable: "ScreenRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScreenRuns_SavedStrategyId_RunAt",
                table: "ScreenRuns",
                columns: new[] { "SavedStrategyId", "RunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_SavedStrategyId_DetectedAt",
                table: "AlertEvents",
                columns: new[] { "SavedStrategyId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_ScreenRunId",
                table: "AlertEvents",
                column: "ScreenRunId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScreenRuns_SavedStrategies_SavedStrategyId",
                table: "ScreenRuns",
                column: "SavedStrategyId",
                principalTable: "SavedStrategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScreenRuns_SavedStrategies_SavedStrategyId",
                table: "ScreenRuns");

            migrationBuilder.DropTable(
                name: "AlertEvents");

            migrationBuilder.DropIndex(
                name: "IX_ScreenRuns_SavedStrategyId_RunAt",
                table: "ScreenRuns");

            migrationBuilder.DropColumn(
                name: "MemberSecuritiesJson",
                table: "ScreenRuns");

            migrationBuilder.DropColumn(
                name: "SavedStrategyId",
                table: "ScreenRuns");
        }
    }
}
