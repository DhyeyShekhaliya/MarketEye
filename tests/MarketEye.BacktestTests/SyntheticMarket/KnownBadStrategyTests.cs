using FluentAssertions;
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
/// PLAN.md §8.3: "Deliberately poor strategies must backtest poorly... If everything you test
/// looks profitable, that is a bug, not alpha." Three scenarios, run against a real SQL Server so
/// this exercises the actual screening + backtest SQL path, not a re-implementation of it:
///
/// 1. Negative earnings + high leverage + high price -- the textbook "everything wrong with this
///    company" screen -- must lose money.
/// 2. Buying the worst momentum (deeply oversold names that keep falling, not bouncing) must lose
///    money. This is deliberately NOT a mean-reversion setup; the point is that "oversold" alone
///    is not a buy signal.
/// 3. An indiscriminate, no-edge basket (every security, weighted equally, selected by no signal
///    correlated with its outcome) must land at exactly its blended average return -- neither
///    inflated nor deflated. This is the sharpest version of "if everything looks profitable,
///    that's a bug": a basket with two winners and two losers has no business returning anything
///    other than their weighted average.
///
/// Every expected value is derived in the test's own comment from the seeded prices, not copied
/// from a run of the code under test.
/// </summary>
public class KnownBadStrategyTests : IAsyncLifetime
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

    private static readonly DateOnly D0 = new(2024, 1, 2);
    private static readonly DateOnly D1 = new(2024, 1, 3);
    private static readonly DateOnly DEnd = new(2024, 6, 28);

    private async Task<int> AddSecurityAsync(string ticker, CancellationToken ct)
    {
        var security = new Security
        {
            Ticker = ticker, ProviderSecurityId = $"INE{ticker}01018", Name = $"{ticker} Ltd",
            Exchange = "NSE", IsActive = true,
        };
        _db.Securities.Add(security);
        await _db.SaveChangesAsync(ct);
        return security.Id;
    }

    private static PriceBar Bar(int securityId, DateOnly date, decimal close) => new()
    {
        SecurityId = securityId, Date = date,
        Open = close, High = close, Low = close, Close = close, AdjClose = close, Volume = 1000,
    };

    private async Task SealD0SnapshotAsync(int priceRowCount, CancellationToken ct)
    {
        var lifecycle = new SnapshotLifecycle(_db);
        var snapshot = await lifecycle.OpenAsync(D0, "known-bad-strategy/1", ct);
        await lifecycle.SealAsync(snapshot.Id, priceRowCount, fundamentalRows: 0, ct);
    }

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

    private static BacktestDefinition BuildDefinition(FilterNode root, decimal initialCapital) => new()
    {
        Criteria = new ScreenCriteria
        {
            Universe = new UniverseConstraint { Exchange = "NSE" },
            Root = root,
        },
        StartDate = D0,
        EndDate = DEnd,
        RebalanceFrequency = RebalanceFrequency.Annual, // one rebalance in this short window
        WeightingMethod = WeightingMethod.EqualWeight,
        InitialCapital = initialCapital,
        ExecutionPrice = ExecutionPriceRule.NextOpen,
        TransactionCostBps = 0, // isolate the selection signal; cost math is tested independently
        SlippageBps = 0,
        BenchmarkTicker = null,
    };

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Negative_earnings_high_leverage_high_price_stocks_lose_money()
    {
        // Two securities that fail every one of the three red flags at once: ReturnOnEquity < 0,
        // DebtToEquity > 3, ClosePrice > 500. Both decline sharply over the window -- exactly the
        // kind of company this screen is supposed to find, and exactly why buying it should hurt.
        var ct = TestContext.Current.CancellationToken;

        var bad1 = await AddSecurityAsync("BAD1", ct);
        var bad2 = await AddSecurityAsync("BAD2", ct);

        var bars = new List<PriceBar>
        {
            Bar(bad1, D0, 600), Bar(bad1, D1, 600), Bar(bad1, DEnd, 300), // -50%
            Bar(bad2, D0, 800), Bar(bad2, D1, 800), Bar(bad2, DEnd, 200), // -75%
        };
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);

        _db.FundamentalRatios.AddRange(
            new FundamentalRatios { SecurityId = bad1, ReportedDate = D0, Roe = -40m, DebtToEquity = 6m, Basis = ReportingBasis.Consolidated },
            new FundamentalRatios { SecurityId = bad2, ReportedDate = D0, Roe = -55m, DebtToEquity = 8m, Basis = ReportingBasis.Consolidated });
        await _db.SaveChangesAsync(ct);
        await SealD0SnapshotAsync(bars.Count, ct);

        var root = new Group
        {
            Op = GroupOperator.And,
            Children =
            [
                new Comparison { Field = "ReturnOnEquity", Operator = ComparisonOperator.LessThan, Value = 0m },
                new Comparison { Field = "DebtToEquity", Operator = ComparisonOperator.GreaterThan, Value = 3m },
                new Comparison { Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 500m },
            ],
        };

        var run = await BuildEngine().RunAsync(BuildDefinition(root, 200_000m), ct);

        run.Rebalances.Should().HaveCount(1);
        run.Rebalances[0].HoldingsJson.Should().Contain("BAD1").And.Contain("BAD2");
        run.FinalEquity.Should().BeLessThan(run.InitialCapital);
        run.CagrNet.Should().BeLessThan(0m);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Buying_the_worst_momentum_loses_money()
    {
        // Two deeply oversold names (Rsi14 well below 15) that keep falling rather than bouncing --
        // "oversold" is not, by itself, a reason to expect a reversal.
        var ct = TestContext.Current.CancellationToken;

        var worst1 = await AddSecurityAsync("WORST1", ct);
        var worst2 = await AddSecurityAsync("WORST2", ct);

        var bars = new List<PriceBar>
        {
            Bar(worst1, D0, 100), Bar(worst1, D1, 100), Bar(worst1, DEnd, 40), // -60%
            Bar(worst2, D0, 50), Bar(worst2, D1, 50), Bar(worst2, DEnd, 15),   // -70%
        };
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);

        _db.Indicators.AddRange(
            new IndicatorSet { SecurityId = worst1, Date = D0, Rsi14 = 8m },
            new IndicatorSet { SecurityId = worst2, Date = D0, Rsi14 = 5m });
        await _db.SaveChangesAsync(ct);
        await SealD0SnapshotAsync(bars.Count, ct);

        var root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison { Field = "Rsi14", Operator = ComparisonOperator.LessThan, Value = 15m }],
        };

        var run = await BuildEngine().RunAsync(BuildDefinition(root, 200_000m), ct);

        run.Rebalances.Should().HaveCount(1);
        run.Rebalances[0].HoldingsJson.Should().Contain("WORST1").And.Contain("WORST2");
        run.FinalEquity.Should().BeLessThan(run.InitialCapital);
        run.CagrNet.Should().BeLessThan(0m);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_indiscriminate_equal_weight_basket_returns_exactly_its_blended_average()
    {
        // The sharpest form of "if everything looks profitable, that's a bug": four securities,
        // two winners and two losers, selected by a filter with no relationship to their outcome
        // (ClosePrice > 0 -- i.e. every security qualifies, standing in for a no-edge/"random"
        // pick per §8.3). Equal-weight 25% each of 200,000 = 50,000 notional per name; every fill
        // price is exactly 100, so each buys exactly 500 shares -- chosen so the final value is
        // exact, not approximate:
        //   UP1   500 * 125 =  62,500  (+25%)
        //   UP2   500 * 115 =  57,500  (+15%)
        //   DOWN1 500 *  80 =  40,000  (-20%)
        //   DOWN2 500 *  88 =  44,000  (-12%)
        //   Total                        204,000
        // A basket with no edge must land here exactly -- not higher (an inflated-return bug) and
        // not lower (a cost/execution bug), given zero configured costs and no other cash events.
        var ct = TestContext.Current.CancellationToken;

        var up1 = await AddSecurityAsync("UP1", ct);
        var up2 = await AddSecurityAsync("UP2", ct);
        var down1 = await AddSecurityAsync("DOWN1", ct);
        var down2 = await AddSecurityAsync("DOWN2", ct);

        var bars = new List<PriceBar>
        {
            Bar(up1, D0, 100), Bar(up1, D1, 100), Bar(up1, DEnd, 125),
            Bar(up2, D0, 100), Bar(up2, D1, 100), Bar(up2, DEnd, 115),
            Bar(down1, D0, 100), Bar(down1, D1, 100), Bar(down1, DEnd, 80),
            Bar(down2, D0, 100), Bar(down2, D1, 100), Bar(down2, DEnd, 88),
        };
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);
        await SealD0SnapshotAsync(bars.Count, ct);

        var root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison { Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 0m }],
        };

        var run = await BuildEngine().RunAsync(BuildDefinition(root, 200_000m), ct);

        run.Rebalances.Should().HaveCount(1);
        run.Rebalances[0].HoldingsJson
            .Should().Contain("UP1").And.Contain("UP2").And.Contain("DOWN1").And.Contain("DOWN2");
        run.FinalEquity.Should().Be(204_000m);
    }
}
