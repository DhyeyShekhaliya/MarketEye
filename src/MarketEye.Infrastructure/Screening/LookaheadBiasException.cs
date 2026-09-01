namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Thrown when a read would return data that was not knowable at the requested as-of date
/// (PLAN.md §8.2).
///
/// This is deliberately an exception rather than a filter or a log line. A silently-filtered
/// lookahead read produces plausible results that are wrong, which is the single failure mode
/// §8 exists to prevent. Loud beats subtly-wrong.
/// </summary>
public sealed class LookaheadBiasException(string message) : InvalidOperationException(message);
