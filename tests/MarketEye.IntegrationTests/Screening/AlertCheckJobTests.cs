using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using MarketEye.Infrastructure.Screening;
using MarketEye.Ingestion.Jobs;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.IntegrationTests.Screening;

/// <summary>
/// PLAN.md §10 Phase 4 "Alerts": a nightly job that checks every saved strategy against the
/// latest sealed snapshot and records entries/exits. Proves the job end to end against a real
/// SQL Server -- <see cref="AlertSetDifferTests"/> already covers the pure diff math in isolation.
/// </summary>
public class AlertCheckJobTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private string _cs = null!;
    private HybridCache _cache = null!;
    private SavedStrategyStore _store = null!;
    private AlertCheckJob _job = null!;
    private SnapshotLifecycle _snapshots = null!;

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

        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, TestContext.Current.CancellationToken);
        _store = new SavedStrategyStore(_db, new ScreenCriteriaValidator(vocab));
        var inner = new ScreeningEngine(_db, new CriteriaCompiler(vocab), new ScreenCriteriaValidator(vocab), _cs);
        var cachedEngine = new CachedScreeningEngine(inner, _db, _cache);
        _snapshots = new SnapshotLifecycle(_db);
        _job = new AlertCheckJob(_db, cachedEngine, _snapshots, NullLogger<AlertCheckJob>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    private static ScreenCriteria CheapCriteria() => new()
    {
        Universe = UniverseConstraint.All,
        Root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison
            {
                Field = "ClosePrice", Operator = ComparisonOperator.LessThan, Value = 5m,
            }],
        },
    };

    private async Task<int> SeedSecurityAsync(string ticker)
    {
        var ct = TestContext.Current.CancellationToken;
        var security = new Security
        {
            Ticker = ticker, ProviderSecurityId = $"INE{ticker}01018",
            Name = ticker + " Ltd", Exchange = "NSE", Sector = "Technology",
        };
        _db.Securities.Add(security);
        await _db.SaveChangesAsync(ct);
        return security.Id;
    }

    private async Task<DataSnapshot> SealDayAsync(DateOnly date, params (int SecurityId, decimal Close)[] closes)
    {
        var ct = TestContext.Current.CancellationToken;
        var bars = closes.Select(c => new PriceBar
        {
            SecurityId = c.SecurityId, Date = date,
            Open = c.Close, High = c.Close, Low = c.Close, Close = c.Close, AdjClose = c.Close, Volume = 1000,
        }).ToList();
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);

        var snap = await _snapshots.OpenAsync(date, "test/1", ct);
        await _snapshots.SealAsync(snap.Id, priceRows: bars.Count, fundamentalRows: 0, ct);
        return snap;
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task First_ever_check_for_a_strategy_raises_no_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await SeedSecurityAsync("SEC_A");
        await SealDayAsync(new DateOnly(2024, 6, 1), (a, 1m));

        await _store.CreateAsync(new SavedStrategyDraft
        {
            Name = "cheap", Criteria = CheapCriteria(),
        }, ct);

        var result = await _job.RunAsync(new DateOnly(2024, 6, 1), ct);

        result.Succeeded.Should().BeTrue();
        result.StrategiesChecked.Should().Be(1);
        result.EventsRaised.Should().Be(0);
        (await _db.AlertEvents.AnyAsync(ct)).Should().BeFalse(
            "there is nothing to diff against on the first-ever run for a strategy");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_membership_change_between_two_checks_raises_entered_and_exited_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await SeedSecurityAsync("SEC_A");
        var b = await SeedSecurityAsync("SEC_B");

        await _store.CreateAsync(new SavedStrategyDraft
        {
            Name = "cheap", Criteria = CheapCriteria(),
        }, ct);

        // Day 1: A is cheap, B is not.
        await SealDayAsync(new DateOnly(2024, 6, 1), (a, 1m), (b, 100m));
        await _job.RunAsync(new DateOnly(2024, 6, 1), ct);

        // Day 2: A is no longer cheap, B now is -- a full swap.
        await SealDayAsync(new DateOnly(2024, 6, 2), (a, 100m), (b, 1m));
        var result = await _job.RunAsync(new DateOnly(2024, 6, 2), ct);

        result.EventsRaised.Should().Be(2);

        var events = await _db.AlertEvents.OrderBy(e => e.Ticker).ToListAsync(ct);
        events.Should().HaveCount(2);
        events.Should().ContainSingle(e => e.SecurityId == a && e.EventType == AlertEventType.Exited);
        events.Should().ContainSingle(e => e.SecurityId == b && e.EventType == AlertEventType.Entered);
        events.Should().OnlyContain(e => e.AsOfDate == new DateOnly(2024, 6, 2));
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_unchanged_membership_between_two_checks_raises_no_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await SeedSecurityAsync("SEC_A");

        await _store.CreateAsync(new SavedStrategyDraft
        {
            Name = "cheap", Criteria = CheapCriteria(),
        }, ct);

        await SealDayAsync(new DateOnly(2024, 6, 1), (a, 1m));
        await _job.RunAsync(new DateOnly(2024, 6, 1), ct);

        await SealDayAsync(new DateOnly(2024, 6, 2), (a, 1m));
        var result = await _job.RunAsync(new DateOnly(2024, 6, 2), ct);

        result.EventsRaised.Should().Be(0);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_strategy_with_criteria_the_vocabulary_no_longer_recognises_is_skipped_not_fatal()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await SeedSecurityAsync("SEC_A");
        await SealDayAsync(new DateOnly(2024, 6, 1), (a, 1m));

        // Simulates a vocabulary edit since this strategy was saved: inserted directly, bypassing
        // SavedStrategyStore's own validation, the same way a concept could be disabled after the
        // fact in the real app.
        _db.SavedStrategies.Add(new SavedStrategy
        {
            Name = "stale",
            CriteriaJson = ScreenCriteriaJson.Serialize(new ScreenCriteria
            {
                Universe = UniverseConstraint.All,
                Root = new Group
                {
                    Op = GroupOperator.And,
                    Children = [new Comparison
                    {
                        Field = "NoSuchMetricAnyMore", Operator = ComparisonOperator.LessThan, Value = 1m,
                    }],
                },
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _store.CreateAsync(new SavedStrategyDraft { Name = "cheap", Criteria = CheapCriteria() }, ct);
        await _db.SaveChangesAsync(ct);

        var result = await _job.RunAsync(new DateOnly(2024, 6, 1), ct);

        result.Succeeded.Should().BeTrue();
        result.StrategiesChecked.Should().Be(1, "the stale strategy is skipped, not fatal to the batch");
    }
}
