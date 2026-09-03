using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MarketEye.Application.Screening;
using MarketEye.Domain.Backtesting;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Backtesting;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using MarketEye.Infrastructure.Screening;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.BacktestTests.SyntheticMarket;

/// <summary>
/// PLAN.md §8.1: the primary defence against a wrong backtester. Three securities, one split, one
/// dividend, one bankruptcy delisting, one rebalance, hand-computed expected values -- run against
/// a real SQL Server so this exercises <see cref="BacktestEngine"/>'s actual SQL path
/// (<c>CriteriaCompiler</c>, <c>BacktestPriceRepository</c>), not a re-implementation of it in test
/// code. Every expected number in this file is derived in the doc comments, not copied from a run
/// of the code under test.
///
/// Deliberately a single rebalance (Annual frequency over a &lt;1-year window): this isolates the
/// four things §8.1 asks for -- point-in-time universe resolution, T+1 fill, split/dividend/
/// delisting handling -- from multi-rebalance reweighting arithmetic, which is already covered by
/// the pure-function tests in <c>MarketEye.UnitTests.Backtesting</c>. Costs are zeroed so the
/// expected values are exact decimal arithmetic, not an approximation -- cost math has its own
/// independent tests (<c>TransactionCostModelTests</c>).
/// </summary>
public class SyntheticMarketEngineTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private string _cs = null!;

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
        await StrategyConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    // --- The synthetic dataset -------------------------------------------------------------
    //
    //   D0 = 2024-01-02  signal date (screen as-of / snapshot date)
    //   D1 = 2024-01-03  execution date (T+1) -- equal-weight buy fills here
    //   D2 = 2024-02-01
    //   D3 = 2024-03-15  ALPHA 2-for-1 split (AdjustmentFactor 0.5)
    //   D4 = 2024-04-10  BETA dividend, INR 2.00/share (chosen to exactly offset BETA's 2-point
    //                    ex-dividend price drop, so total return is provably unaffected)
    //   D5 = 2024-05-20  GAMMA delists (bankruptcy) -- exits at zero
    //   D6 = 2024-06-28  end date
    //
    // InitialCapital 300,000 / 3 securities = 100,000 target notional each, chosen so every fill
    // price divides it evenly (no fractional shares to complicate the hand computation).

    private static readonly DateOnly D0 = new(2024, 1, 2);
    private static readonly DateOnly D1 = new(2024, 1, 3);
    private static readonly DateOnly D2 = new(2024, 2, 1);
    private static readonly DateOnly D3 = new(2024, 3, 15);
    private static readonly DateOnly D4 = new(2024, 4, 10);
    private static readonly DateOnly D5 = new(2024, 5, 20);
    private static readonly DateOnly D6 = new(2024, 6, 28);

    private async Task<(int Alpha, int Beta, int Gamma)> SeedMarketAsync(CancellationToken ct)
    {
        var alpha = new Security
        {
            Ticker = "ALPHA", ProviderSecurityId = "INEALPHA01018", Name = "Alpha Ltd",
            Exchange = "NSE", IsActive = true,
        };
        var beta = new Security
        {
            Ticker = "BETA", ProviderSecurityId = "INEBETA001018", Name = "Beta Ltd",
            Exchange = "NSE", IsActive = true,
        };
        // Known to delist at D5, bankrupt -- set up front, exactly as a real synthetic fixture
        // (and ScreeningPipelineTests' own GAMMA fixture) does: the point of §7's survivorship
        // guard is that a screen run at D0 must still include a security delisted after D0.
        var gamma = new Security
        {
            Ticker = "GAMMA", ProviderSecurityId = "INEGAMMA01018", Name = "Gamma Ltd",
            Exchange = "NSE", IsActive = false, DelistedDate = D5, DelistingReason = DelistingReason.Bankruptcy,
        };
        _db.Securities.AddRange(alpha, beta, gamma);
        await _db.SaveChangesAsync(ct);

        var bars = new List<PriceBar>
        {
            Bar(alpha.Id, D0, 100), Bar(alpha.Id, D1, 100), Bar(alpha.Id, D2, 120),
            Bar(alpha.Id, D3, 60), Bar(alpha.Id, D4, 65), Bar(alpha.Id, D5, 70), Bar(alpha.Id, D6, 80),

            Bar(beta.Id, D0, 50), Bar(beta.Id, D1, 50), Bar(beta.Id, D2, 55),
            Bar(beta.Id, D3, 55), Bar(beta.Id, D4, 53), Bar(beta.Id, D5, 54), Bar(beta.Id, D6, 56),

            // GAMMA needs no bar after D1: it exits at zero on D5 regardless (bankruptcy), and D5
            // already appears in the trading calendar via ALPHA/BETA's own bars.
            Bar(gamma.Id, D0, 25), Bar(gamma.Id, D1, 25), Bar(gamma.Id, D2, 27), Bar(gamma.Id, D3, 27), Bar(gamma.Id, D4, 27),
        };
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);

        _db.CorporateActions.Add(new CorporateAction
        {
            SecurityId = alpha.Id, EffectiveDate = D3, ActionType = CorporateActionType.Split,
            AdjustmentFactor = 0.5m, RawDescription = "2-for-1 split",
        });
        _db.CorporateActions.Add(new CorporateAction
        {
            SecurityId = beta.Id, EffectiveDate = D4, ActionType = CorporateActionType.Dividend,
            DividendAmount = 2.00m, RawDescription = "INR 2.00 dividend",
        });
        await _db.SaveChangesAsync(ct);

        var lifecycle = new SnapshotLifecycle(_db);
        var snapshot = await lifecycle.OpenAsync(D0, "synthetic/1", ct);
        await lifecycle.SealAsync(snapshot.Id, priceRows: bars.Count, fundamentalRows: 0, ct);

        return (alpha.Id, beta.Id, gamma.Id);
    }

    private static PriceBar Bar(int securityId, DateOnly date, decimal close) => new()
    {
        SecurityId = securityId, Date = date,
        Open = close, High = close, Low = close, Close = close, AdjClose = close, Volume = 1000,
    };

    private BacktestEngine BuildEngine()
    {
        var vocab = DbMetricConceptVocabulary.LoadAsync(_db, TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
        var compiler = new CriteriaCompiler(vocab);
        var validator = new ScreenCriteriaValidator(vocab);
        var screeningEngine = new ScreeningEngine(_db, compiler, validator, _cs);
        var snapshots = new SnapshotLifecycle(_db);
        var priceRepo = new BacktestPriceRepository(_cs);
        var fillExecutor = new FillExecutor(priceRepo);

        return new BacktestEngine(
            _db, screeningEngine, snapshots, priceRepo, fillExecutor, NullLogger<BacktestEngine>.Instance);
    }

    private static BacktestDefinition BuildDefinition() => new()
    {
        Criteria = new ScreenCriteria
        {
            Universe = new UniverseConstraint { Exchange = "NSE" },
            Root = new Group
            {
                Op = GroupOperator.And,
                Children = [new Comparison { Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 0m }],
            },
        },
        StartDate = D0,
        EndDate = D6,
        RebalanceFrequency = RebalanceFrequency.Annual,
        WeightingMethod = WeightingMethod.EqualWeight,
        InitialCapital = 300_000m,
        ExecutionPrice = ExecutionPriceRule.NextOpen,
        TransactionCostBps = 0,
        SlippageBps = 0,
        BenchmarkTicker = null,
    };

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task The_synthetic_market_produces_exactly_one_rebalance_with_all_three_securities()
    {
        // §7's survivorship requirement, exercised end to end: GAMMA is IsActive=false with a
        // FUTURE DelistedDate relative to the signal date, and must still appear in the screen
        // that runs at D0 -- exactly what CriteriaCompiler's delisted-inclusive join is for.
        var ct = TestContext.Current.CancellationToken;
        await SeedMarketAsync(ct);
        var engine = BuildEngine();

        var run = await engine.RunAsync(BuildDefinition(), ct);

        run.Rebalances.Should().HaveCount(1);
        run.Rebalances[0].SignalDate.Should().Be(D0);
        run.Rebalances[0].ExecutionDate.Should().Be(D1);
        run.Rebalances[0].HoldingsJson.Should().Contain("ALPHA").And.Contain("BETA").And.Contain("GAMMA");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Final_equity_matches_the_hand_computed_value()
    {
        // Hand computation (see the class doc comment for the full per-date derivation):
        //   Equal-weight buy at D1: 1000 ALPHA @100, 2000 BETA @50, 4000 GAMMA @25 -- 100,000 each.
        //   D3 split: ALPHA shares 1000 / 0.5 = 2000 (continuous value, no fake drop).
        //   D4 dividend: cash += 2.00 * 2000 BETA shares = 4,000.
        //   D5 bankruptcy: GAMMA exits at zero (4000 shares * 0).
        //   D6 final marks: ALPHA 2000*80 = 160,000; BETA 2000*56 = 112,000; cash = 4,000.
        //   Total = 160,000 + 112,000 + 4,000 = 276,000.
        var ct = TestContext.Current.CancellationToken;
        await SeedMarketAsync(ct);
        var engine = BuildEngine();

        var run = await engine.RunAsync(BuildDefinition(), ct);

        run.FinalEquity.Should().Be(276_000m);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Gross_and_net_returns_are_equal_when_costs_are_zero()
    {
        // A parallel-simulation gross/net implementation (ADR-0009) must agree exactly with a
        // zero-cost net simulation -- if these ever diverge with zero costs configured, the two
        // simulations are not actually equivalent at zero cost, which would be a bug in the
        // parallel-run design, not a rounding artefact.
        var ct = TestContext.Current.CancellationToken;
        await SeedMarketAsync(ct);
        var engine = BuildEngine();

        var run = await engine.RunAsync(BuildDefinition(), ct);

        run.CagrGross.Should().Be(run.CagrNet);
        run.TotalCostsPaid.Should().Be(0m);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_stock_split_does_not_create_a_fake_value_drop()
    {
        // THE regression this test exists to catch: the engine marks positions at raw Close
        // (§4.4, §7), which legitimately halves on ALPHA's split date. Without adjusting the held
        // share count in step, the portfolio would show an artificial ~17% drop on D3 that never
        // happened economically (ALPHA's 120,000 D2 value must still read 120,000 on D3).
        var ct = TestContext.Current.CancellationToken;
        await SeedMarketAsync(ct);
        var engine = BuildEngine();

        var run = await engine.RunAsync(BuildDefinition(), ct);

        // BacktestEngine serialises the curve with JsonSerializerDefaults.Web (camelCase); the
        // default Deserialize options are case-sensitive, so this must match or every property
        // silently binds to its default value instead of throwing.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var curve = System.Text.Json.JsonSerializer.Deserialize<List<CurvePointDto>>(run.EquityCurveJson, jsonOptions)!;
        var atD2 = curve.Single(p => p.Date == D2).Nav;
        var atD3 = curve.Single(p => p.Date == D3).Nav;

        atD3.Should().Be(atD2, "a split changes share count and price together; portfolio value must be unaffected");
        atD2.Should().Be(338_000m); // 1000*120 + 2000*55 + 4000*27
    }

    private sealed record CurvePointDto(DateOnly Date, decimal Nav);
}
