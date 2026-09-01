using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketEye.Infrastructure.MarketData.IndianApi;

/// <summary>
/// Extracts adjustment ratios from the provider's free-text <c>remarks</c> field.
///
/// The provider supplies bonus, split and rights actions with the ratio embedded in prose —
/// "Bonus issue in the ratio of 1:1 of Rs. 10/-." — rather than as structured numbers. There is no
/// alternative source, so it has to be parsed.
///
/// **Every method returns null when it is not certain.** ADR-0004 explains why: a misread bonus
/// ratio halves or doubles every historical price for that security. A missing adjustment leaves a
/// visible discontinuity in the price series that someone will notice; a wrong one produces a
/// smooth, plausible series that is silently incorrect. Given the choice, fail visibly.
/// </summary>
public static partial class CorporateActionRatioParser
{
    /// <summary>
    /// Bonus: "in the ratio of A:B" means A free shares for every B held.
    /// Factor = B / (A + B). A 1:1 bonus gives 0.5 — the same as a 2-for-1 split.
    /// </summary>
    public static decimal? BonusFactor(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks)) return null;

        var m = RatioPattern().Match(remarks);
        if (!m.Success) return null;

        if (!TryDec(m.Groups["a"].Value, out var free) || !TryDec(m.Groups["b"].Value, out var held))
            return null;
        if (free <= 0 || held <= 0) return null;

        return held / (free + held);
    }

    /// <summary>
    /// Split: quoted as a face-value change, "from Rs. 10 to Rs. 5". The price scales by the same
    /// proportion the face value does, so factor = new / old.
    ///
    /// Deliberately does NOT fall back to the generic A:B pattern. Split remarks and bonus remarks
    /// both contain colon-separated numbers, and reading a face-value change as a share ratio (or
    /// the reverse) is precisely the inversion this class exists to prevent.
    /// </summary>
    public static decimal? SplitFactor(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks)) return null;

        var m = FaceValuePattern().Match(remarks);
        if (!m.Success) return null;

        if (!TryDec(m.Groups["from"].Value, out var oldFv) || !TryDec(m.Groups["to"].Value, out var newFv))
            return null;
        if (oldFv <= 0 || newFv <= 0) return null;

        return newFv / oldFv;
    }

    /// <summary>
    /// Rights need the subscription price and the cum-rights market price to compute TERP, and the
    /// remarks carry at most the ratio and premium. Returning null is correct rather than lazy:
    /// the caller has the price series and must compute the factor with
    /// <c>AdjustmentFactors.ForRights</c>.
    /// </summary>
    public static RightsTerms? RightsTerms(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks)) return null;

        var m = RatioPattern().Match(remarks);
        if (!m.Success) return null;
        if (!TryDec(m.Groups["a"].Value, out var offered) || !TryDec(m.Groups["b"].Value, out var held))
            return null;
        if (offered <= 0 || held <= 0) return null;

        decimal? price = null;
        var p = PricePattern().Match(remarks);
        if (p.Success && TryDec(p.Groups["price"].Value, out var parsed)) price = parsed;

        return new RightsTerms(offered, held, price);
    }

    private static bool TryDec(string s, out decimal value) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    // "ratio of 1:1", "ratio of 3 : 5". Anchored on the word "ratio" so a stray colon elsewhere
    // in the sentence cannot be mistaken for the ratio.
    [GeneratedRegex(@"ratio\s+of\s+(?<a>\d+(?:\.\d+)?)\s*:\s*(?<b>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RatioPattern();

    // "from Rs. 10 to Rs. 5", "from Rs10/- to Rs2/-"
    // "Re." is the Indian singular of "Rs." and appears in essentially every split TO one rupee:
    // "from Rs. 2/- to Re. 1/-". Matching only "Rs" silently failed those, leaving the split
    // unadjusted and a real step in the price series.
    [GeneratedRegex(@"from\s+(?:r[se]\.?\s*)?(?<from>\d+(?:\.\d+)?)\s*(?:/-)?\s*to\s+(?:r[se]\.?\s*)?(?<to>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex FaceValuePattern();

    [GeneratedRegex(@"(?:at|premium\s+of)\s+(?:r[se]\.?\s*)?(?<price>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex PricePattern();
}

/// <summary>Ratio terms for a rights issue. The factor still needs the cum-rights market price.</summary>
public sealed record RightsTerms(decimal Offered, decimal Held, decimal? SubscriptionPrice);
