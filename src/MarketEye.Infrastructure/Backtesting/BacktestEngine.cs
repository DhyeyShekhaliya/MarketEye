using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MarketEye.Application.Backtesting;
using MarketEye.Domain.Backtesting;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Screening;

namespace MarketEye.Infrastructure.Backtesting;

/// <summary>
/// The rebalance loop (PLAN.md §7), in the exact order §7 specifies. A thin orchestrator: universe
/// resolution and point-in-time correctness are delegated entirely to <see cref="ScreeningEngine"/>
/// and <see cref="SnapshotLifecycle"/> (already built for Phase 1/2), and every calculation with no
/// database dependency lives in the pure `MarketEye.Application.Backtesting` functions. This class
/// exists to sequence those pieces and own the mutable simulation state, mirroring how
/// `DailyIngestionJob` orchestrates `PriceBarBulkWriter`/`SnapshotLifecycle` rather than inlining
/// raw SQL of its own.
///
/// Depends on the plain <see cref="ScreeningEngine"/>, never `CachedScreeningEngine` — a backtest
/// replays many distinct historical dates that would each be a one-time cache key anyway (comment
/// already recorded in `InfrastructureServiceCollectionExtensions`).
/// </summary>
public sealed class BacktestEngine(
    MarketEyeDbContext db,
    ScreeningEngine screeningEngine,
    SnapshotLifecycle snapshots,
    BacktestPriceRepository priceRepo,
    FillExecutor fillExecutor,
    ILogger<BacktestEngine> logger)
{
    private static readonly JsonSerializerOptions BlobJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BacktestRun> RunAsync(BacktestDefinition definition, CancellationToken ct)
    {
        if (definition.StartDate > definition.EndDate)
        {
            throw new ArgumentException(
                $"Start date {definition.StartDate:yyyy-MM-dd} must be on or before end date " +
                $"{definition.EndDate:yyyy-MM-dd}.");
        }

        var sw = Stopwatch.StartNew();

        // One simulation, at the configured (real) costs. An earlier version ran a SECOND,
        // independent zero-cost simulation for the gross figure — but weight-based rebalancing
        // resizes every trade against the portfolio's CURRENT value, so a lower net-of-costs value
        // at rebalance N produces a genuinely different share count than the zero-cost run from
        // rebalance N onward, not just a cash offset. Over several rebalances the two simulations'
        // trading paths diverge enough that net could come back HIGHER than gross -- economically
        // impossible with non-negative costs, and confirmed live against real multi-year data
        // before this fix. Gross is instead derived by adding cumulative costs paid back onto the
        // SAME net trading path, which guarantees CagrNet <= CagrGross by construction, since the
        // only thing separating the two curves at any point is the (non-negative) costs paid so far.
        var net = await SimulateAsync(definition, ct);

        var netCurve = net.EquityCurve.Select(p => p.Nav).ToList();
        var grossCurve = net.EquityCurve.Zip(net.CumulativeCostsAtPoint, (p, c) => p.Nav + c).ToList();
        var finalEquity = netCurve.Count > 0 ? netCurve[^1] : definition.InitialCapital;
        var finalEquityGross = grossCurve.Count > 0 ? grossCurve[^1] : definition.InitialCapital;

        var days = definition.EndDate.DayNumber - definition.StartDate.DayNumber;
        var cagrNet = BacktestMetricsCalculator.Cagr(definition.InitialCapital, finalEquity, days);
        var cagrGross = BacktestMetricsCalculator.Cagr(definition.InitialCapital, finalEquityGross, days);

        var dailyReturns = BacktestMetricsCalculator.DailyReturns(netCurve);
        var maxDrawdown = BacktestMetricsCalculator.MaxDrawdown(netCurve);
        var sharpe = BacktestMetricsCalculator.Sharpe(dailyReturns);
        var sortino = BacktestMetricsCalculator.Sortino(dailyReturns);
        var winRate = BacktestMetricsCalculator.WinRate(dailyReturns);
        var annualTurnover = BacktestMetricsCalculator.AnnualTurnover(
            net.TurnoverPerRebalance, definition.RebalanceFrequency);

        var (benchmarkCurveJson, benchmarkCagr) = await BuildBenchmarkCurveAsync(definition, ct);

        var run = new BacktestRun
        {
            DefinitionJson = BacktestDefinitionJson.Serialize(definition),
            RunAt = DateTimeOffset.UtcNow,
            StartDate = definition.StartDate,
            EndDate = definition.EndDate,
            InitialCapital = definition.InitialCapital,
            FinalEquity = finalEquity,
            CagrGross = cagrGross,
            CagrNet = cagrNet,
            MaxDrawdown = maxDrawdown,
            Sharpe = sharpe,
            Sortino = sortino,
            WinRate = winRate,
            AnnualTurnover = annualTurnover,
            TotalCostsPaid = net.TotalCostsPaid,
            BenchmarkTicker = definition.BenchmarkTicker,
            BenchmarkCagr = benchmarkCagr,
            EquityCurveJson = JsonSerializer.Serialize(net.EquityCurve, BlobJsonOptions),
            BenchmarkCurveJson = benchmarkCurveJson,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Rebalances = net.Rebalances,
        };

        db.BacktestRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>
    /// §7's benchmark comparison. Missing benchmark data is not an error (§7's "config value, not
    /// an interface" decision extends to graceful absence) — the run still completes with a null
    /// benchmark curve/CAGR, and the UI says data was unavailable rather than the request failing.
    /// </summary>
    private async Task<(string? CurveJson, decimal? Cagr)> BuildBenchmarkCurveAsync(
        BacktestDefinition definition, CancellationToken ct)
    {
        if (definition.BenchmarkTicker is not { } ticker) return (null, null);

        var prices = await priceRepo.GetBenchmarkPricesAsync(ticker, definition.StartDate, definition.EndDate, ct);
        if (prices.Count < 2)
        {
            logger.LogWarning(
                "No benchmark data for {Ticker} in [{Start}, {End}]; the run will have no benchmark comparison.",
                ticker, definition.StartDate, definition.EndDate);
            return (null, null);
        }

        var basis = prices[0].TotalReturnIndexValue;
        var rebased = prices
            .Select(p => new EquityPoint(
                p.Date, basis == 0 ? 0m : p.TotalReturnIndexValue / basis * definition.InitialCapital))
            .ToList();

        var days = prices[^1].Date.DayNumber - prices[0].Date.DayNumber;
        var cagr = BacktestMetricsCalculator.Cagr(
            prices[0].TotalReturnIndexValue, prices[^1].TotalReturnIndexValue, days);

        return (JsonSerializer.Serialize(rebased, BlobJsonOptions), cagr);
    }

    // --- The simulation itself ------------------------------------------------------------------

    private sealed class Position
    {
        public decimal Shares;
    }

    private sealed class PriceTrack
    {
        public decimal LastPrice;
        public int ConsecutiveMissingDays;
    }

    private sealed record SimulationResult(
        List<EquityPoint> EquityCurve,
        List<decimal> CumulativeCostsAtPoint,
        List<BacktestRebalance> Rebalances,
        decimal TotalCostsPaid,
        List<decimal> TurnoverPerRebalance);

    /// <summary>Runs the full §7 rebalance loop once, at the definition's configured costs.</summary>
    private async Task<SimulationResult> SimulateAsync(BacktestDefinition definition, CancellationToken ct)
    {
        var transactionCostBps = definition.TransactionCostBps;
        var slippageBps = definition.SlippageBps;
        var rebalanceDates = RebalanceScheduler.Dates(definition.StartDate, definition.EndDate, definition.RebalanceFrequency);

        var cash = definition.InitialCapital;
        var positions = new Dictionary<int, Position>();
        var priceTrack = new Dictionary<int, PriceTrack>();
        var equityCurve = new List<EquityPoint>();
        var cumulativeCostsAtPoint = new List<decimal>();
        var rebalances = new List<BacktestRebalance>();
        var turnoverPerRebalance = new List<decimal>();
        var totalCosts = 0m;

        // Keeps equityCurve and cumulativeCostsAtPoint in lockstep -- every point on the curve
        // needs to know the total costs paid up to that moment, so RunAsync can derive the gross
        // curve (nav + cumulative costs) without re-simulating.
        void AddPoint(DateOnly date, decimal nav)
        {
            equityCurve.Add(new EquityPoint(date, nav));
            cumulativeCostsAtPoint.Add(totalCosts);
        }

        AddPoint(definition.StartDate, definition.InitialCapital);

        for (var i = 0; i < rebalanceDates.Count; i++)
        {
            var signalDate = rebalanceDates[i];
            var periodEnd = i + 1 < rebalanceDates.Count ? rebalanceDates[i + 1] : definition.EndDate;

            var snapshot = await snapshots.LatestSealedAsync(signalDate, ct);
            if (snapshot is null)
            {
                logger.LogWarning(
                    "No sealed snapshot at or before {SignalDate}; skipping this rebalance.", signalDate);
                continue;
            }

            var criteria = definition.MaxPositions is { } max
                ? definition.Criteria with { Limit = max }
                : definition.Criteria;
            var screenResult = await screeningEngine.RunAsync(criteria, snapshot, ct);
            var targetIds = screenResult.Rows.Select(r => r.Id).ToList();
            var tickerById = screenResult.Rows.ToDictionary(r => r.Id, r => r.Ticker);

            var executionDate = await priceRepo.NextTradingDateAsync(snapshot.AsOfDate, ct);
            if (executionDate is null || executionDate > definition.EndDate)
            {
                logger.LogWarning(
                    "No trading day after {SignalDate} within the backtest window; skipping this rebalance.",
                    signalDate);
                continue;
            }
            PointInTimeGuard.RequireExecutionAfterSignal(snapshot.AsOfDate, executionDate.Value);

            var relevantIds = positions.Keys.Union(targetIds).ToList();
            var securities = await priceRepo.GetSecuritiesAsync(relevantIds, ct);

            var currentValue = cash + positions.Sum(p => p.Value.Shares * PriceOf(p.Key, priceTrack));
            var currentWeights = positions.ToDictionary(
                p => p.Key, p => currentValue == 0 ? 0m : (p.Value.Shares * PriceOf(p.Key, priceTrack)) / currentValue);
            var targetWeights = PortfolioWeighting.EqualWeight(targetIds);

            var trades = TradeListBuilder.Diff(currentWeights, targetWeights, currentValue);
            var notionalTraded = 0m;
            var costsThisRebalance = 0m;

            foreach (var trade in trades)
            {
                var fill = await fillExecutor.TryFillAsync(
                    trade.SecurityId, executionDate.Value, periodEnd,
                    MissingPriceCarryForward.MaxCarryForwardDays, ct);

                if (fill is null)
                {
                    logger.LogWarning(
                        "Security {SecurityId} had no fillable (non-circuit-locked) price within " +
                        "{Days} trading days of {ExecutionDate}; the trade was dropped.",
                        trade.SecurityId, MissingPriceCarryForward.MaxCarryForwardDays, executionDate);
                    continue;
                }

                var dollarAmount = trade.DeltaWeight * currentValue;
                var sharesDelta = fill.Price == 0 ? 0m : dollarAmount / fill.Price;

                if (!positions.TryGetValue(trade.SecurityId, out var position))
                {
                    position = new Position();
                    positions[trade.SecurityId] = position;
                }
                position.Shares += sharesDelta;
                if (position.Shares <= 0)
                {
                    positions.Remove(trade.SecurityId);
                    priceTrack.Remove(trade.SecurityId);
                }
                else
                {
                    priceTrack[trade.SecurityId] = new PriceTrack { LastPrice = fill.Price, ConsecutiveMissingDays = 0 };
                }

                cash -= dollarAmount;
                var cost = TransactionCostModel.Cost(trade.Notional, transactionCostBps, slippageBps);
                cash -= cost;
                costsThisRebalance += cost;
                notionalTraded += trade.Notional;
            }

            totalCosts += costsThisRebalance;
            turnoverPerRebalance.Add(currentValue == 0 ? 0m : notionalTraded / currentValue);

            var postTradeValue = cash + positions.Sum(p => p.Value.Shares * PriceOf(p.Key, priceTrack));

            if (equityCurve[^1].Date != executionDate.Value)
            {
                AddPoint(executionDate.Value, postTradeValue);
            }

            var holdings = positions.Select(p => new HoldingSnapshot(
                p.Key,
                securities.TryGetValue(p.Key, out var s) ? s.Ticker : tickerById.GetValueOrDefault(p.Key, "?"),
                postTradeValue == 0 ? 0m : (p.Value.Shares * PriceOf(p.Key, priceTrack)) / postTradeValue,
                p.Value.Shares,
                PriceOf(p.Key, priceTrack))).ToList();

            rebalances.Add(new BacktestRebalance
            {
                SignalDate = snapshot.AsOfDate,
                ExecutionDate = executionDate.Value,
                CashAfter = cash,
                PortfolioValueAfter = postTradeValue,
                CostsPaid = costsThisRebalance,
                TurnoverPct = currentValue == 0 ? 0m : notionalTraded / currentValue,
                HoldingsJson = JsonSerializer.Serialize(holdings, BlobJsonOptions),
            });

            // §7 steps 8-10: walk day by day from the day AFTER execution through periodEnd
            // (inclusive), accruing dividends, closing out delistings, and carrying forward or
            // force-exiting on missing prices. The trading calendar is the union of bar dates for
            // every security this leg could possibly touch — there is no separate exchange
            // calendar table, and this is exactly the set that matters to this portfolio's NAV.
            if (positions.Count > 0 && executionDate.Value < periodEnd)
            {
                var walkStart = executionDate.Value.AddDays(1);
                var barsById = await priceRepo.GetBarsAsync(positions.Keys.ToList(), walkStart, periodEnd, ct);
                var dividends = await priceRepo.GetDividendsAsync(positions.Keys.ToList(), walkStart, periodEnd, ct);
                var shareActions = await priceRepo.GetShareAdjustingActionsAsync(positions.Keys.ToList(), walkStart, periodEnd, ct);

                var tradingDays = barsById.Values.SelectMany(b => b.Select(x => x.Date)).Distinct().OrderBy(d => d).ToList();
                var barByDay = new Dictionary<(int SecurityId, DateOnly Date), PriceBar>();
                foreach (var (secId, bars) in barsById)
                {
                    foreach (var bar in bars) barByDay[(secId, bar.Date)] = bar;
                }
                var dividendsByDay = dividends
                    .GroupBy(d => d.EffectiveDate)
                    .ToDictionary(g => g.Key, g => g.ToList());
                var shareActionsByDay = shareActions
                    .GroupBy(a => a.EffectiveDate)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var day in tradingDays)
                {
                    // Split/bonus/rights: the engine marks positions at RAW Close (§4.4, §7), which
                    // legitimately steps down on the ex-date because that is what actually traded.
                    // The held share count must step up in the same proportion or the position
                    // would show a fake value drop that never happened economically. Applied BEFORE
                    // the day's price mark so PriceOf below already reflects the adjusted holding.
                    if (shareActionsByDay.TryGetValue(day, out var todaysShareActions))
                    {
                        foreach (var action in todaysShareActions)
                        {
                            if (positions.TryGetValue(action.SecurityId, out var pos) && action.AdjustmentFactor is > 0)
                            {
                                pos.Shares /= action.AdjustmentFactor.Value;
                            }
                        }
                    }

                    // Dividends accrue into cash on the ex-date, per share currently held (using
                    // the post-adjustment share count above, so a split and a dividend landing on
                    // the same date still pay the correct total).
                    if (dividendsByDay.TryGetValue(day, out var todaysDividends))
                    {
                        foreach (var div in todaysDividends)
                        {
                            if (positions.TryGetValue(div.SecurityId, out var pos) && div.DividendAmount is { } amount)
                            {
                                cash += amount * pos.Shares;
                            }
                        }
                    }

                    // Delisting exits: last price, or zero for bankruptcy (§7). Unknown -- the
                    // only reason DelistingDetector ever actually writes -- exits at last price,
                    // same as every non-bankruptcy reason.
                    foreach (var secId in positions.Keys.ToList())
                    {
                        if (!securities.TryGetValue(secId, out var sec) || sec.DelistedDate != day) continue;

                        var exitPrice = sec.DelistingReason == DelistingReason.Bankruptcy
                            ? 0m
                            : (barByDay.TryGetValue((secId, day), out var exitBar) ? exitBar.Close : PriceOf(secId, priceTrack));

                        cash += positions[secId].Shares * exitPrice;
                        positions.Remove(secId);
                        priceTrack.Remove(secId);
                        logger.LogInformation(
                            "Security {SecurityId} delisted on {Date} ({Reason}); exited at {Price}.",
                            secId, day, sec.DelistingReason, exitPrice);
                    }

                    // Missing-price carry-forward for everything still held.
                    foreach (var secId in positions.Keys.ToList())
                    {
                        var todaysBar = barByDay.GetValueOrDefault((secId, day));
                        var track = priceTrack.TryGetValue(secId, out var t) ? t : new PriceTrack();
                        var decision = MissingPriceCarryForward.Resolve(todaysBar?.Close, track.LastPrice, track.ConsecutiveMissingDays);

                        if (decision.ForceExit)
                        {
                            cash += positions[secId].Shares * track.LastPrice;
                            positions.Remove(secId);
                            priceTrack.Remove(secId);
                            logger.LogWarning(
                                "Security {SecurityId} missing a price for {Days}+ trading days as of {Date}; force-exited at {Price}.",
                                secId, MissingPriceCarryForward.MaxCarryForwardDays, day, track.LastPrice);
                            continue;
                        }

                        priceTrack[secId] = new PriceTrack
                        {
                            LastPrice = decision.Price!.Value,
                            ConsecutiveMissingDays = todaysBar is null ? track.ConsecutiveMissingDays + 1 : 0,
                        };
                    }

                    var nav = cash + positions.Sum(p => p.Value.Shares * PriceOf(p.Key, priceTrack));
                    AddPoint(day, nav);
                }
            }
        }

        if (equityCurve[^1].Date != definition.EndDate)
        {
            var finalValue = cash + positions.Sum(p => p.Value.Shares * PriceOf(p.Key, priceTrack));
            AddPoint(definition.EndDate, finalValue);
        }

        return new SimulationResult(equityCurve, cumulativeCostsAtPoint, rebalances, totalCosts, turnoverPerRebalance);
    }

    private static decimal PriceOf(int securityId, Dictionary<int, PriceTrack> priceTrack) =>
        priceTrack.TryGetValue(securityId, out var track) ? track.LastPrice : 0m;

    private sealed record HoldingSnapshot(int SecurityId, string Ticker, decimal Weight, decimal Shares, decimal Price);
}
