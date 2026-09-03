using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// The §8.2 bias guards, enforced in the repository layer.
///
/// §8.2 is explicit that these must fail loudly rather than be upheld by convention or code
/// review. Every point-in-time read passes through here before it reaches the database.
/// </summary>
public static class PointInTimeGuard
{
    /// <summary>Reads must resolve against a sealed snapshot (§4.5).</summary>
    public static void RequireSealed(DataSnapshot snapshot)
    {
        if (snapshot.SealedAt is null)
        {
            throw new LookaheadBiasException(
                $"Snapshot {snapshot.Id} is not sealed. Screens and backtests resolve against " +
                "sealed snapshots only (§4.5); an open snapshot is still being written to.");
        }
    }

    /// <summary>
    /// A read may never reach past the as-of date. Catches the case where a caller passes a
    /// requested date later than the snapshot it is reading — the shape a "just get me today's
    /// price" convenience call quietly takes.
    /// </summary>
    public static void RequireNotAfterAsOf(DateOnly requested, DateOnly asOf, string what)
    {
        if (requested > asOf)
        {
            throw new LookaheadBiasException(
                $"Refusing to read {what} for {requested:yyyy-MM-dd} against an as-of date of " +
                $"{asOf:yyyy-MM-dd}. That data did not exist yet (§8.2).");
        }
    }

    /// <summary>
    /// Fundamentals need BOTH conditions (§4.1). This guards the reporting-lag half: a filing is
    /// invisible until its ReportedDate, no matter which fiscal period it covers.
    /// </summary>
    public static void RequireReportedBy(DateOnly reportedDate, DateOnly asOf, int securityId)
    {
        if (reportedDate > asOf)
        {
            throw new LookaheadBiasException(
                $"Fundamentals for security {securityId} were reported on " +
                $"{reportedDate:yyyy-MM-dd}, after the as-of date {asOf:yyyy-MM-dd}. " +
                "Reading them is lookahead bias (§4.1, §8.2).");
        }
    }

    /// <summary>
    /// §7: a screen using data as of T fills at T+1's open. Filling at T's close uses information
    /// from the same session the decision was made in.
    /// </summary>
    public static void RequireExecutionAfterSignal(DateOnly signalDate, DateOnly executionDate)
    {
        if (executionDate <= signalDate)
        {
            throw new LookaheadBiasException(
                $"Execution date {executionDate:yyyy-MM-dd} is not after signal date " +
                $"{signalDate:yyyy-MM-dd}. §7 requires T+1 execution; filling at T's close is lookahead.");
        }
    }

    /// <summary>
    /// §8.2: the historical universe must INCLUDE securities that have since delisted, and asserts
    /// that they ARE included rather than merely allowing them.
    ///
    /// This is the inverse of the other guards — it fires when the caller filtered too much rather
    /// than too little. It needs the full known set to compare against, because "what is missing"
    /// is not answerable from the result alone. That is precisely why survivorship bias is easy to
    /// ship: nothing in the returned data looks wrong.
    /// </summary>
    /// <param name="universe">The universe the caller assembled.</param>
    /// <param name="allKnownSecurities">Every security in the snapshot, delisted included.</param>
    /// <param name="asOf">The as-of date the universe claims to represent.</param>
    public static void RequireDelistedIncluded(
        IReadOnlyCollection<Security> universe,
        IReadOnlyCollection<Security> allKnownSecurities,
        DateOnly asOf)
    {
        var included = universe.Select(s => s.Id).ToHashSet();

        // Tradeable at asOf means: still active, or delisted strictly after that date.
        var wronglyExcluded = allKnownSecurities
            .Where(s => s.IsActive || (s.DelistedDate is { } d && d > asOf))
            .Where(s => !included.Contains(s.Id))
            .ToList();

        if (wronglyExcluded.Count > 0)
        {
            var sample = string.Join(", ", wronglyExcluded.Take(5).Select(s => s.Ticker));
            throw new LookaheadBiasException(
                $"{wronglyExcluded.Count} securities tradeable on {asOf:yyyy-MM-dd} are missing " +
                $"from the universe (e.g. {sample}). Excluding securities that delisted later is " +
                "survivorship bias (§7, §8.2).");
        }
    }

    /// <summary>
    /// §7 revision 3: a circuit-locked stock cannot be filled at that price. This is a backstop
    /// invariant, not the control flow — the fill logic checks <see cref="PriceBar.IsCircuitLocked"/>
    /// itself and branches into the skip-and-carry-forward path BEFORE ever constructing a fill,
    /// the same "re-check, don't trust" pattern <c>CriteriaCompiler</c> already uses for a sealed
    /// snapshot. This guard exists to catch a future refactor that builds a fill without checking.
    /// </summary>
    public static void RequireNotCircuitLocked(PriceBar bar)
    {
        if (bar.IsCircuitLocked)
        {
            throw new LookaheadBiasException(
                $"Security {bar.SecurityId} was circuit-locked on {bar.Date:yyyy-MM-dd}. A locked " +
                "stock cannot be filled at that price (§7 revision 3); the caller must skip the " +
                "fill and carry the trade forward instead of constructing an execution here.");
        }
    }
}
