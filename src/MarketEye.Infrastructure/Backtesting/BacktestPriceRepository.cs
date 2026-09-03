using Dapper;
using Microsoft.Data.SqlClient;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Backtesting;

/// <summary>
/// Raw Dapper reads <c>BacktestEngine</c> needs that <c>ScreeningEngine</c> doesn't expose.
///
/// A screen only ever returns the latest bar as of one date; the rebalance loop needs full-period
/// series (bars between rebalances, dividends within a holding period, the next trading date after
/// a signal). Reads, not writes — the ingest path's Dapper/SqlBulkCopy split (§3, ADR-0002) is
/// about writes; this mirrors <c>ScreeningEngine</c>'s read-side Dapper usage instead.
/// </summary>
public sealed class BacktestPriceRepository(string connectionString)
{
    /// <summary>Every bar for the given securities within [from, to], inclusive, grouped by security.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PriceBar>>> GetBarsAsync(
        IReadOnlyCollection<int> securityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return new Dictionary<int, IReadOnlyList<PriceBar>>();

        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<PriceBar>(new CommandDefinition("""
            SELECT SecurityId, [Date], [Open], High, Low, [Close], AdjClose, Volume, IsCircuitLocked
            FROM dbo.PriceBars
            WHERE SecurityId IN @ids AND [Date] BETWEEN @from AND @to
            ORDER BY SecurityId, [Date];
            """, new { ids = securityIds, from, to }, cancellationToken: ct));

        return rows.GroupBy(r => r.SecurityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PriceBar>)g.ToList());
    }

    /// <summary>Dividend-paying corporate actions for the given securities within [from, to].</summary>
    public async Task<IReadOnlyList<CorporateAction>> GetDividendsAsync(
        IReadOnlyCollection<int> securityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return [];

        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<CorporateAction>(new CommandDefinition("""
            SELECT Id, SecurityId, EffectiveDate, ActionType, AdjustmentFactor, DividendAmount,
                   NewTicker, RawDescription
            FROM dbo.CorporateActions
            WHERE SecurityId IN @ids AND EffectiveDate BETWEEN @from AND @to
              AND DividendAmount IS NOT NULL
            ORDER BY SecurityId, EffectiveDate;
            """, new { ids = securityIds, from, to }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Share-count-adjusting corporate actions (Split/Bonus/Rights) for the given securities
    /// within [from, to] — distinct from <see cref="GetDividendsAsync"/>, which is cash-only.
    ///
    /// The engine trades and marks positions at RAW Close (§4.4, §7), never AdjClose, so a split
    /// or bonus is not automatically reflected in a held position's value: raw Close legitimately
    /// steps down on the ex-date because that is what actually traded, and the share count must
    /// step up in the same proportion or the position's value would show a fake drop. Rights
    /// issues are treated the same way in v1 (share count scaled by 1/AdjustmentFactor) as a
    /// documented simplification — it does not model the subscription cash outflow a real rights
    /// issue requires from a participating holder.
    /// </summary>
    public async Task<IReadOnlyList<CorporateAction>> GetShareAdjustingActionsAsync(
        IReadOnlyCollection<int> securityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return [];

        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<CorporateAction>(new CommandDefinition("""
            SELECT Id, SecurityId, EffectiveDate, ActionType, AdjustmentFactor, DividendAmount,
                   NewTicker, RawDescription
            FROM dbo.CorporateActions
            WHERE SecurityId IN @ids AND EffectiveDate BETWEEN @from AND @to
              AND ActionType IN ('Split', 'Bonus', 'Rights') AND AdjustmentFactor IS NOT NULL
            ORDER BY SecurityId, EffectiveDate;
            """, new { ids = securityIds, from, to }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// The earliest trading date strictly after <paramref name="after"/> — any security's bar
    /// qualifies, since the market as a whole either trades on a date or it does not.
    /// </summary>
    public async Task<DateOnly?> NextTradingDateAsync(DateOnly after, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
            "SELECT MIN([Date]) FROM dbo.PriceBars WHERE [Date] > @after;",
            new { after }, cancellationToken: ct));
    }

    /// <summary>Security rows for delisting-exit and universe-membership decisions, keyed by id.</summary>
    public async Task<IReadOnlyDictionary<int, Security>> GetSecuritiesAsync(
        IReadOnlyCollection<int> securityIds, CancellationToken ct)
    {
        if (securityIds.Count == 0) return new Dictionary<int, Security>();

        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<Security>(new CommandDefinition("""
            SELECT Id, Ticker, ProviderSecurityId, Name, Exchange, Sector, Industry,
                   IsActive, DelistedDate, DelistingReason
            FROM dbo.Securities
            WHERE Id IN @ids;
            """, new { ids = securityIds }, cancellationToken: ct));

        return rows.ToDictionary(s => s.Id);
    }

    /// <summary>Benchmark total-return index values for a ticker within [from, to] (§7, ADR-0010).</summary>
    public async Task<IReadOnlyList<BenchmarkPrice>> GetBenchmarkPricesAsync(
        string ticker, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<BenchmarkPrice>(new CommandDefinition("""
            SELECT Ticker, [Date], TotalReturnIndexValue
            FROM dbo.BenchmarkPrices
            WHERE Ticker = @ticker AND [Date] BETWEEN @from AND @to
            ORDER BY [Date];
            """, new { ticker, from, to }, cancellationToken: ct));

        return rows.ToList();
    }
}
