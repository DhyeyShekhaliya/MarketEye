using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StrategyConcepts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultOperator",
                table: "MetricConcepts");

            migrationBuilder.DropColumn(
                name: "DefaultThreshold",
                table: "MetricConcepts");

            migrationBuilder.CreateTable(
                name: "StrategyConcepts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    AliasesCsv = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyConcepts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyConcepts_Name_OwnerUserId",
                table: "StrategyConcepts",
                columns: new[] { "Name", "OwnerUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyConcepts");

            migrationBuilder.AddColumn<string>(
                name: "DefaultOperator",
                table: "MetricConcepts",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultThreshold",
                table: "MetricConcepts",
                type: "decimal(28,6)",
                precision: 28,
                scale: 6,
                nullable: true);
        }
    }
}
