using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.MarketData;

namespace MarketEye.Infrastructure.Ai;

/// <summary>
/// Orchestrates one natural-language parse (PLAN.md §5.4).
///
/// The rate limiter runs in the ASP.NET Core pipeline before a request reaches this class, so the
/// pipeline here is just: cache lookup -> on miss, consume the daily budget, call the model,
/// resolve, validate.
///
/// Depends on <see cref="IIntentParser"/> only, never on MarketEye.Ai. RepositoryLayoutTests
/// enforces the reverse direction (Ai must not reach Infrastructure); keeping this side clean too
/// is what makes §2's claim -- "the model can be swapped, removed, or fail entirely and the system
/// below it still works" -- true rather than aspirational. This class does not know NVIDIA NIM
/// exists.
/// </summary>
public sealed class IntentTranslationService(
    IIntentParser parser,
    IntentResolver resolver,
    IStrategyConceptVocabulary strategies,
    HybridCache cache,
    RequestBudget budget,
    int dailyCallCap,
    ILogger<IntentTranslationService> logger)
{
    /// <summary>
    /// RequestBudget partitions by this string, not by model vendor -- it names the FEATURE being
    /// rationed, so switching provider (NVIDIA NIM today, per AiOptions) does not reset or
    /// fragment an in-progress day's budget.
    /// </summary>
    private const string BudgetProvider = "ai-intent-parser";

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        // NvidiaIntentParser calls the model at temperature 0 for reproducibility, so the same
        // normalised prompt against the same vocabulary version always resolves the same way. A
        // full day is safe to cache and matches the once-a-day cadence data changes elsewhere
        // (§5.5) -- this is ParseCache's half of that two-level cache.
        Expiration = TimeSpan.FromHours(24),
        LocalCacheExpiration = TimeSpan.FromHours(24),
    };

    public async Task<IntentResolution> TranslateAsync(string prompt, CancellationToken ct)
    {
        var normalised = Normalise(prompt);
        if (normalised.Length == 0)
        {
            return IntentResolution.Ask(
                "Type a screening idea, such as \"cheap profitable small caps that aren't overbought\".");
        }

        // Including the vocabulary version means editing a definition invalidates every cached
        // parse by construction -- the same free-invalidation trick SnapshotId gives the §5.5
        // result cache, reused here for repeat phrasings.
        var cacheKey = $"parse:{strategies.VersionToken}:{Hash(normalised)}";

        ParsedIntent intent;
        try
        {
            intent = await cache.GetOrCreateAsync(
                cacheKey,
                factory: ct2 => new ValueTask<ParsedIntent>(ParseFreshAsync(prompt, ct2)),
                options: CacheOptions,
                cancellationToken: ct);
        }
        catch (IntentUnavailableException ex)
        {
            // HybridCache never stores a value when the factory throws. That is exactly what an
            // exhausted budget or a down provider needs: today's failure must not poison
            // tomorrow's identical prompt once the provider or the budget recovers.
            logger.LogInformation("Intent parsing unavailable: {Reason}", ex.Message);
            return IntentResolution.Ask(
                $"Natural-language parsing is temporarily unavailable ({ex.Message}). Use the " +
                "manual filters below, or try again shortly.");
        }

        return resolver.Resolve(intent);
    }

    private async Task<ParsedIntent> ParseFreshAsync(string prompt, CancellationToken ct)
    {
        // Checked before the call, the same posture RequestBudget's own doc-comment argues for:
        // discovering the quota is gone after paying for the call wastes it. Skipped entirely for
        // a parser that consumes no budget (the stub), so the free fallback cannot be disabled by
        // its own usage.
        if (parser.ConsumesBudget &&
            !await budget.TryConsumeAsync(BudgetProvider, dailyCallCap, 1, ct))
        {
            throw new IntentUnavailableException("the daily AI parsing budget is exhausted for today");
        }

        var outcome = await parser.ParseAsync(prompt, ct);
        return outcome switch
        {
            ParseOutcome.Parsed p => p.Intent,
            ParseOutcome.Unavailable u => throw new IntentUnavailableException(u.Reason),
            _ => throw new InvalidOperationException($"Unhandled {nameof(ParseOutcome)} case."),
        };
    }

    private static string Normalise(string prompt) =>
        Regex.Replace(prompt.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string Hash(string normalisedPrompt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalisedPrompt));
        return Convert.ToHexString(bytes, 0, 16).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Signals "do not cache this attempt" to <see cref="TranslateAsync"/>'s catch block without
    /// HybridCache ever seeing a value to store.
    /// </summary>
    private sealed class IntentUnavailableException(string reason) : Exception(reason);
}
