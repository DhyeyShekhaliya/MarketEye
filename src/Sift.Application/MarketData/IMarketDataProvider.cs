using Sift.Domain.Entities;

namespace Sift.Application.MarketData;

/// <summary>
/// The single seam to an external market-data vendor (PLAN.md §3).
///
/// Deliberately not chosen yet: §12 names historical backfill against provider rate
/// limits as the real Phase 1 blocker and requires that analysis before ingestion code
/// is written. Phase 0 provides this interface and a fixture-backed stub.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>Provider name and version, recorded on the DataSnapshot (§4.5).</summary>
    string ProviderVersion { get; }

    /// <summary>
    /// The investable universe. Must include securities that have since delisted —
    /// omitting them is survivorship bias (§7).
    /// </summary>
    Task<IReadOnlyList<SecurityDto>> GetSecuritiesAsync(CancellationToken ct);

    /// <summary>
    /// Daily bars. Close and AdjClose are returned separately and must stay separate:
    /// execution uses raw Close/Open, returns use AdjClose (§4.4).
    /// </summary>
    Task<IReadOnlyList<PriceBarDto>> GetPriceBarsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Reported figures. ReportedDate is required — without it §4.1 cannot be honoured.</summary>
    Task<IReadOnlyList<FundamentalsDto>> GetFundamentalsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Splits, dividends, ticker changes, delistings (§4.3).</summary>
    Task<IReadOnlyList<CorporateActionDto>> GetCorporateActionsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct);
}

public record SecurityDto(
    string ProviderSecurityId, string Ticker, string Name, string Exchange,
    string? Sector, string? Industry, bool IsActive,
    DateOnly? DelistedDate, DelistingReason? DelistingReason);

/// <summary>
/// One daily bar. <paramref name="Close"/> is what actually traded — use it for display
/// and execution. <paramref name="AdjClose"/> is split- and dividend-adjusted — use it for
/// return calculation. Conflating them makes every multi-year backtest wrong (§4.4).
/// </summary>
public record PriceBarDto(
    DateOnly Date, decimal Open, decimal High, decimal Low,
    decimal Close, decimal AdjClose, long Volume);

public record FundamentalsDto(
    DateOnly FiscalPeriodEnd, DateOnly ReportedDate,
    decimal? Revenue, decimal? NetIncome, decimal? TotalDebt, decimal? ShareholdersEquity);

public record CorporateActionDto(
    DateOnly EffectiveDate, CorporateActionType ActionType,
    decimal? SplitRatio, decimal? DividendAmount, string? NewTicker);

public enum CorporateActionType
{
    Split = 0,
    Dividend = 1,
    TickerChange = 2,
    Merger = 3,
    Delisting = 4,
}
