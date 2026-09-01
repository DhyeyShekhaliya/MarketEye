using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MarketEye.Infrastructure.Persistence;
using Testcontainers.MsSql;
using Xunit;
using MarketEye.IntegrationTests;

namespace MarketEye.IntegrationTests.Persistence;

/// <summary>
/// Applies the real migrations to a real SQL Server and asserts the shape PLAN.md §4.1
/// requires. This runs against EF's own output rather than hand-written DDL, so it fails
/// if a future migration silently drops system versioning.
/// </summary>
public class TemporalSchemaTests : IAsyncLifetime
{
    // Passing the image to the constructor: the parameterless MsSqlBuilder() is obsolete
    // in Testcontainers 4.14.
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _sql.StartAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<MarketEyeDbContext>()
            .UseSqlServer(_sql.GetConnectionString())
            .Options;
        await using var db = new MarketEyeDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _sql.DisposeAsync();

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Fundamentals_is_system_versioned_with_a_history_table()
    {
        await using var conn = new SqlConnection(_sql.GetConnectionString());

        var rows = (await conn.QueryAsync<(string Name, int TemporalType)>(
            "SELECT name, temporal_type FROM sys.tables WHERE name IN ('Fundamentals','FundamentalsHistory')"))
            .ToDictionary(r => r.Name, r => r.TemporalType);

        rows.Should().ContainKey("Fundamentals").WhoseValue.Should().Be(2,
            "2 = SYSTEM_VERSIONED_TEMPORAL_TABLE, required by PLAN.md §4.1");
        rows.Should().ContainKey("FundamentalsHistory").WhoseValue.Should().Be(1,
            "1 = HISTORY_TABLE");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Point_in_time_read_needs_both_conditions()
    {
        await using var conn = new SqlConnection(_sql.GetConnectionString());

        await conn.ExecuteAsync(
            """
            INSERT INTO Securities (Ticker, ProviderSecurityId, Name, Exchange, IsActive)
            VALUES ('AAA', 'FIX-0001', 'Alpha Industries', 'NYSE', 1);
            """);
        var securityId = await conn.QuerySingleAsync<int>(
            "SELECT Id FROM Securities WHERE ProviderSecurityId = 'FIX-0001'");

        // Fiscal period ends 2024-03-31 but the market only learns it on 2024-05-02.
        await conn.ExecuteAsync(
            """
            INSERT INTO Fundamentals (SecurityId, FiscalPeriodEnd, ReportedDate, Revenue)
            VALUES (@securityId, '2024-03-31', '2024-05-02', 1250000.00);
            """, new { securityId });

        // A screen run on 2024-04-15 must NOT see it: the period had ended, but the
        // filing had not happened. This is the reporting-lag half of §4.1, and the half
        // that FOR SYSTEM_TIME alone does not cover.
        var visibleTooEarly = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM Fundamentals WHERE SecurityId = @securityId AND ReportedDate <= '2024-04-15'",
            new { securityId });
        visibleTooEarly.Should().Be(0, "the filing had not been published on 2024-04-15");

        var visibleAfter = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM Fundamentals WHERE SecurityId = @securityId AND ReportedDate <= '2024-05-31'",
            new { securityId });
        visibleAfter.Should().Be(1);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Restating_a_figure_preserves_the_original_in_history()
    {
        await using var conn = new SqlConnection(_sql.GetConnectionString());

        await conn.ExecuteAsync(
            """
            INSERT INTO Securities (Ticker, ProviderSecurityId, Name, Exchange, IsActive)
            VALUES ('BBB', 'FIX-0002', 'Beta Software', 'NASDAQ', 1);
            """);
        var securityId = await conn.QuerySingleAsync<int>(
            "SELECT Id FROM Securities WHERE ProviderSecurityId = 'FIX-0002'");

        await conn.ExecuteAsync(
            """
            INSERT INTO Fundamentals (SecurityId, FiscalPeriodEnd, ReportedDate, NetIncome)
            VALUES (@securityId, '2024-06-30', '2024-08-01', 195000.00);
            """, new { securityId });

        var asOfBeforeRestatement = DateTime.UtcNow;
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // The company restates earnings downward.
        await conn.ExecuteAsync(
            "UPDATE Fundamentals SET NetIncome = 120000.00 WHERE SecurityId = @securityId",
            new { securityId });

        var current = await conn.QuerySingleAsync<decimal>(
            "SELECT NetIncome FROM Fundamentals WHERE SecurityId = @securityId", new { securityId });
        current.Should().Be(120000.00m);

        // A backtest as of the earlier date must still see the ORIGINAL figure. Reading
        // today's restated number would be lookahead bias (§4.1).
        var historical = await conn.QuerySingleAsync<decimal>(
            $"SELECT NetIncome FROM Fundamentals FOR SYSTEM_TIME AS OF '{asOfBeforeRestatement:yyyy-MM-dd HH:mm:ss.fff}' WHERE SecurityId = @securityId",
            new { securityId });
        historical.Should().Be(195000.00m,
            "the backtest must see what was known then, not what is known now");
    }
}
