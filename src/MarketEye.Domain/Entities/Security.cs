namespace MarketEye.Domain.Entities;

/// <summary>
/// A tradeable equity. Rows are never deleted when a security delists — see
/// <see cref="IsActive"/>. PLAN.md §7 requires delisted securities to stay in the
/// universe, so removing them here would reintroduce survivorship bias at the source.
/// </summary>
public class Security
{
    public int Id { get; set; }

    /// <summary>
    /// Current ticker. Not a stable identity: tickers get reused and reassigned.
    /// PLAN.md §4.4 requires reconciliation on <see cref="ProviderSecurityId"/> so a
    /// ticker change does not create a second row.
    /// </summary>
    public required string Ticker { get; set; }

    /// <summary>Provider-stable identifier — the real identity key (§4.4).</summary>
    public required string ProviderSecurityId { get; set; }

    public required string Name { get; set; }
    public required string Exchange { get; set; }
    public string? Sector { get; set; }
    public string? Industry { get; set; }

    /// <summary>False once delisted. The row stays; §7 depends on that.</summary>
    public bool IsActive { get; set; } = true;

    public DateOnly? DelistedDate { get; set; }

    /// <summary>
    /// Why it left the universe. The backtester exits at the last traded price, or at
    /// zero for bankruptcy (§7) — so the reason is load-bearing, not descriptive.
    /// </summary>
    public DelistingReason? DelistingReason { get; set; }
}

/// <summary>Drives backtest exit pricing (§7).</summary>
public enum DelistingReason
{
    Unknown = 0,
    Acquisition = 1,
    Merger = 2,
    /// <summary>Exit at zero, not at last price.</summary>
    Bankruptcy = 3,
    ExchangeRuleViolation = 4,
    GoingPrivate = 5,
}
