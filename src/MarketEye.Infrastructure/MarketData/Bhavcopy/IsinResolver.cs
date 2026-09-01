using Microsoft.Extensions.Logging;

namespace MarketEye.Infrastructure.MarketData.Bhavcopy;

/// <summary>
/// Resolves a symbol to a stable security identifier (PLAN.md §4.4).
///
/// §4.4 requires reconciliation on a provider-stable id so a ticker change does not create a
/// second Security row. NSE's older bhavcopy layouts carry an ISIN and that works perfectly. The
/// `sec_bhavdata_full` layout, which is what public archives carry from mid-2021 onward, **does
/// not** — so identity has to be recovered rather than read.
///
/// The recovery: older ISIN-bearing files still exist in the same archives, so a symbol → ISIN map
/// is built from them once and reused. Symbols absent from that map (companies listed after the
/// ISIN-bearing files end) fall back to a synthetic `NSE:SYMBOL` id.
///
/// **Known limitation, deliberately visible.** For a fallback-id security, a later ticker change
/// WILL create a second row, because there is nothing stable to match on. This is a real gap in
/// §4.4's guarantee for that subset, not a hidden one — it is logged, counted, and reported by
/// <see cref="FallbackCount"/> so the size of the gap is known rather than assumed to be zero.
/// </summary>
public sealed class IsinResolver(ILogger<IsinResolver> logger)
{
    private readonly Dictionary<string, string> _symbolToIsin = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fallbacks = new(StringComparer.OrdinalIgnoreCase);

    public int MappedCount => _symbolToIsin.Count;
    public int FallbackCount => _fallbacks.Count;

    /// <summary>
    /// Learns symbol → ISIN pairs from any parsed rows that carry one. Safe to call repeatedly;
    /// later files win, which is what you want when a symbol has been reassigned.
    /// </summary>
    public void Learn(IEnumerable<BhavcopyRow> rows)
    {
        foreach (var r in rows)
        {
            if (r.Isin.Length > 0 && r.Symbol.Length > 0) _symbolToIsin[r.Symbol] = r.Isin;
        }
    }

    /// <summary>Returns the ISIN when known, otherwise a clearly-marked synthetic id.</summary>
    public string Resolve(BhavcopyRow row)
    {
        if (row.Isin.Length > 0) return row.Isin;
        if (_symbolToIsin.TryGetValue(row.Symbol, out var isin)) return isin;

        if (_fallbacks.Add(row.Symbol))
        {
            logger.LogDebug(
                "No ISIN for {Symbol}; using a synthetic id. Ticker changes for this security " +
                "cannot be reconciled (§4.4).", row.Symbol);
        }
        return $"NSE:{row.Symbol}";
    }

    /// <summary>True when the id was synthesised rather than read from the exchange's own data.</summary>
    public static bool IsSynthetic(string providerSecurityId) =>
        providerSecurityId.StartsWith("NSE:", StringComparison.Ordinal);
}
