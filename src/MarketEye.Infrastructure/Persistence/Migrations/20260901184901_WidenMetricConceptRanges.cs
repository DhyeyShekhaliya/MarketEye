using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketEye.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WidenMetricConceptRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "MinValue",
                table: "MetricConcepts",
                type: "decimal(28,6)",
                precision: 28,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxValue",
                table: "MetricConcepts",
                type: "decimal(28,6)",
                precision: 28,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "DefaultThreshold",
                table: "MetricConcepts",
                type: "decimal(28,6)",
                precision: 28,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,6)",
                oldPrecision: 18,
                oldScale: 6,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "MinValue",
                table: "MetricConcepts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,6)",
                oldPrecision: 28,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "MaxValue",
                table: "MetricConcepts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,6)",
                oldPrecision: 28,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "DefaultThreshold",
                table: "MetricConcepts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,6)",
                oldPrecision: 28,
                oldScale: 6,
                oldNullable: true);
        }
    }
}
