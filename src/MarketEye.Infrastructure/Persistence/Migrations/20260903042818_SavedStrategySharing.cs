using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SavedStrategySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "SavedStrategies",
                type: "nvarchar(43)",
                maxLength: 43,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SharedAt",
                table: "SavedStrategies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SavedStrategyId",
                table: "BacktestRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedStrategies_ShareToken",
                table: "SavedStrategies",
                column: "ShareToken",
                unique: true,
                filter: "[ShareToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_SavedStrategyId_RunAt",
                table: "BacktestRuns",
                columns: new[] { "SavedStrategyId", "RunAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_BacktestRuns_SavedStrategies_SavedStrategyId",
                table: "BacktestRuns",
                column: "SavedStrategyId",
                principalTable: "SavedStrategies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacktestRuns_SavedStrategies_SavedStrategyId",
                table: "BacktestRuns");

            migrationBuilder.DropIndex(
                name: "IX_SavedStrategies_ShareToken",
                table: "SavedStrategies");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_SavedStrategyId_RunAt",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "ShareToken",
                table: "SavedStrategies");

            migrationBuilder.DropColumn(
                name: "SharedAt",
                table: "SavedStrategies");

            migrationBuilder.DropColumn(
                name: "SavedStrategyId",
                table: "BacktestRuns");
        }
    }
}
