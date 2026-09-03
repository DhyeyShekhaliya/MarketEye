namespace MarketEye.Application.Backtesting;

/// <summary>
/// §7 step 10: a security with no bar on a given trading day (a data gap, not a delisting) is
/// carried forward at its last known price for up to 5 trading days, then force-exited and logged.
/// Kept as a pure decision function — the caller supplies today's close (or null) and how many
/// consecutive days have already been carried, and this returns what to do next, with no I/O and
/// no mutable state of its own.
/// </summary>
public static class MissingPriceCarryForward
{
    public const int MaxCarryForwardDays = 5;

    public readonly record struct Decision(decimal? Price, bool ForceExit);

    public static Decision Resolve(decimal? todaysClose, decimal lastKnownPrice, int consecutiveMissingDays)
    {
        if (todaysClose is { } close) return new Decision(close, ForceExit: false);

        return consecutiveMissingDays < MaxCarryForwardDays
            ? new Decision(lastKnownPrice, ForceExit: false)
            : new Decision(null, ForceExit: true);
    }
}
