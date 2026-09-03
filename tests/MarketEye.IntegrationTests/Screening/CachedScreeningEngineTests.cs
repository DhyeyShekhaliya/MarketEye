using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using MarketEye.Infrastructure.Screening;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.IntegrationTests.Screening;

/// <summary>
/// Proves ScreenResultCache (§5.5) against a real query, not just the key-building logic: the
/// second identical request must not touch SQL again, and the run history must still record it.
/// </summary>
public class CachedScreeningEngineTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private string _cs = null!;
    private HybridCache _cache = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;

        DapperTypeHandlers.Register();
        await _sql.StartAsync(TestContext.Current.CancellationToken);
        _cs = _sql.GetConnectionString();

        _db = new MarketEyeDbContext(
            new DbContextOptionsBuilder<MarketEyeDbContext>().UseSqlServer(_cs).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await MetricConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddHybridCache();
        _cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    public async ValueTask DisposeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    private async Task<DataSnapshot> BuildSealedSnapshotAsync(DateOnly date, string ticker)
    {
        var ct = TestContext.Current.CancellationToken;

        var security = new Security
        {
            Ticker = ticker, ProviderSecurityId = $"INE{ticker}01018",
            Name = ticker + " Ltd", Exchange = "NSE", Sector = "Technology",
        };
        _db.Securities.Add(security);
        await _db.SaveChangesAsync(ct);

        await new PriceBarBulkWriter(_cs).WriteAsync([new PriceBar
        {
            SecurityId = security.Id, Date = date,
            Open = 100, High = 100, Low = 100, Close = 100, AdjClose = 100, Volume = 1000,
        }], ct);

        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(date, "test/1", ct);

        // SealAsync returns Task, not the snapshot -- but it re-fetches by id through the SAME
        // tracked DbContext, so EF's identity map means this mutates the snap instance in place.
        await lifecycle.SealAsync(snap.Id, priceRows: 1, fundamentalRows: 0, ct);
        return snap;
    }

    private async Task<CachedScreeningEngine> BuildEngineAsync()
    {
        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, TestContext.Current.CancellationToken);
        var inner = new ScreeningEngine(_db, new CriteriaCompiler(vocab), new ScreenCriteriaValidator(vocab), _cs);
        return new CachedScreeningEngine(inner, _db, _cache);
    }

    private static ScreenCriteria Criteria() => new()
    {
        Universe = UniverseConstraint.All,
        Root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison
            {
                Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 1m,
            }],
        },
    };

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Repeating_the_same_screen_is_answered_from_cache_not_sql()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = await BuildSealedSnapshotAsync(new DateOnly(2024, 6, 1), "ALPHA");
        var engine = await BuildEngineAsync();
        var criteria = Criteria();

        var first = await engine.RunAsync(criteria, snapshot, null, ct);
        var second = await engine.RunAsync(criteria, snapshot, null, ct);

        second.Rows.Select(r => r.Ticker).Should().BeEquivalentTo(first.Rows.Select(r => r.Ticker),
            "a cache hit must return the same data a fresh execution would");

        // The cached VALUE, not just the ScreenRuns row, must say so -- otherwise an API caller
        // sees the first run's stale DurationMs repeated forever with no way to tell hits apart.
        first.FromCache.Should().BeFalse();
        second.FromCache.Should().BeTrue();
        second.DurationMs.Should().Be(0);

        // Distinguishes "ran once" from "ran twice and happened to agree": each RunAsync writes
        // its own ScreenRun row (ScreeningEngine on a miss, CachedScreeningEngine itself on a
        // hit), so two calls must leave exactly one of each kind, never two misses.
        var runs = await _db.ScreenRuns.Where(r => r.SnapshotId == snapshot.Id)
            .OrderBy(r => r.RunAt).ToListAsync(ct);
        runs.Should().HaveCount(2);
        runs[0].FromCache.Should().BeFalse("the first call has nothing cached yet");
        runs[1].FromCache.Should().BeTrue("the second call is the same criteria against the same snapshot");
        runs[1].DurationMs.Should().Be(0, "a cache hit never touched the database, so there is nothing to time");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_new_snapshot_is_a_fresh_miss_even_for_identical_criteria()
    {
        // §4.5: a new SnapshotId invalidates every cached result by construction. Serving the OLD
        // snapshot's cached rows here would be silently ignoring a day's worth of new data.
        var ct = TestContext.Current.CancellationToken;
        var first = await BuildSealedSnapshotAsync(new DateOnly(2024, 6, 1), "BETA1");
        var second = await BuildSealedSnapshotAsync(new DateOnly(2024, 6, 2), "BETA2");
        var engine = await BuildEngineAsync();
        var criteria = Criteria();

        await engine.RunAsync(criteria, first, null, ct);
        await engine.RunAsync(criteria, second, null, ct);

        var secondSnapshotRuns = await _db.ScreenRuns
            .Where(r => r.SnapshotId == second.Id).ToListAsync(ct);
        secondSnapshotRuns.Should().ContainSingle()
            .Which.FromCache.Should().BeFalse("this snapshot's key has never been cached before");
    }
}
