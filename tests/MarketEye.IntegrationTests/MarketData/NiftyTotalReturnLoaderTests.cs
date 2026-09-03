using FluentAssertions;
using Microsoft.Data.SqlClient;
using Dapper;
using MarketEye.Infrastructure.MarketData.Benchmark;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.IntegrationTests.MarketData;

/// <summary>
/// PLAN.md §10 Phase 4 "Additional benchmarks": the loader was generalised from a hardcoded
/// "NIFTY50TR" ticker to a caller-supplied one, so a second benchmark (NIFTY 500 TR, say) is just
/// a different value passed here, not a code change (`docs/adr/0010`).
/// </summary>
public class NiftyTotalReturnLoaderTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private string _cs = null!;
    private string _csvPath = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;

        DapperTypeHandlers.Register();
        await _sql.StartAsync(TestContext.Current.CancellationToken);
        _cs = _sql.GetConnectionString();

        _db = new MarketEyeDbContext(
            new DbContextOptionsBuilder<MarketEyeDbContext>().UseSqlServer(_cs).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _csvPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(_csvPath, "Date,Close\n01-Sep-2021,20821.87\n02-Sep-2021,20913.53\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
        if (File.Exists(_csvPath)) File.Delete(_csvPath);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Rows_are_stored_under_the_ticker_the_caller_passed_not_a_hardcoded_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var loader = new NiftyTotalReturnLoader(_cs);

        var written = await loader.LoadAsync("NIFTY500TR", _csvPath, ct);

        written.Should().Be(2);

        await using var conn = new SqlConnection(_cs);
        var tickers = (await conn.QueryAsync<string>(
            "SELECT DISTINCT Ticker FROM dbo.BenchmarkPrices")).ToList();
        tickers.Should().BeEquivalentTo(["NIFTY500TR"],
            "the loader must never fall back to the old hardcoded NIFTY50TR ticker");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Loading_two_different_tickers_keeps_both_independently_queryable()
    {
        var ct = TestContext.Current.CancellationToken;
        var loader = new NiftyTotalReturnLoader(_cs);

        await loader.LoadAsync("NIFTY50TR", _csvPath, ct);
        await loader.LoadAsync("NIFTY500TR", _csvPath, ct);

        await using var conn = new SqlConnection(_cs);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT Ticker) FROM dbo.BenchmarkPrices");
        count.Should().Be(2, "a second benchmark must be additive, never overwrite the first ticker's rows");
    }
}
