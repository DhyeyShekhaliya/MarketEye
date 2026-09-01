namespace MarketEye.Domain.Screening;

/// <summary>
/// The internal representation between human intent and SQL (PLAN.md §6).
///
/// Everything downstream of the validator is deterministic. The model produces this shape and
/// nothing else — never SQL — so injection is structurally impossible rather than defended
/// against (§6).
/// </summary>
public sealed record ScreenCriteria
{
    public required UniverseConstraint Universe { get; init; }
    public required FilterNode Root { get; init; }
    public SortSpec? Sort { get; init; }

    /// <summary>Result cap. Null means the engine's default applies.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Which securities are eligible before any filter runs.
///
/// This is where survivorship correctness is decided: a screen run as of a past date must
/// reconstruct membership as it stood then, including securities that have since delisted (§7).
/// </summary>
public sealed record UniverseConstraint
{
    /// <summary>e.g. "NSE". Null means every exchange in the snapshot.</summary>
    public string? Exchange { get; init; }

    /// <summary>e.g. "NIFTY50". Null means no index restriction.</summary>
    public string? Index { get; init; }

    /// <summary>Null means every sector.</summary>
    public string? Sector { get; init; }

    public static UniverseConstraint All => new();
}

public sealed record SortSpec
{
    /// <summary>A concept name, validated against the same whitelist as comparison fields.</summary>
    public required string Field { get; init; }
    public required SortDirection Direction { get; init; }
}

public enum SortDirection
{
    Ascending = 0,
    Descending = 1,
}
