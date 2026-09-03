using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Wraps <see cref="ScreeningEngine"/> with ScreenResultCache (§5.5).
///
/// Kept as a separate decorator, not folded into ScreeningEngine itself, so the engine stays
/// cache-free and unit-testable without a HybridCache in its constructor -- the same reasoning
/// that keeps IntentResolver free of IIntentParser.
///
/// The key includes SnapshotId, so a new ingestion invalidates every cached result by
/// construction (§4.5) -- no TTL to guess, matching ParseCache's version-token trick in §5.4.
/// </summary>
public sealed class CachedScreeningEngine(
    ScreeningEngine inner,
    MarketEyeDbContext db,
    HybridCache cache)
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        // Data changes once a day (§5.5). A snapshot never mutates once sealed, so nothing here
        // ever goes stale before the key itself changes -- the expiration only bounds memory, not
        // correctness.
        Expiration = TimeSpan.FromDays(7),
        LocalCacheExpiration = TimeSpan.FromDays(7),
    };

    public async Task<ScreenResult> RunAsync(
        ScreenCriteria criteria, DataSnapshot snapshot, int? savedStrategyId, CancellationToken ct)
    {
        var key = BuildKey(criteria, snapshot.Id);
        var missed = false;

        var result = await cache.GetOrCreateAsync(
            key,
            factory: async ct2 =>
            {
                // Runs only on a miss. ScreeningEngine.RunAsync already writes its own ScreenRun
                // row for this execution, so nothing more is needed here on that path.
                missed = true;
                return await inner.RunAsync(criteria, snapshot, savedStrategyId, ct2);
            },
            options: CacheOptions,
            cancellationToken: ct);

        if (!missed)
        {
            // A cache hit means inner.RunAsync did not run, so no ScreenRun row exists for this
            // request. Skipping the write here would make the run history quietly undercount how
            // often a screen is actually used -- and, when a saved strategy is behind this call,
            // would make the alert-check job see no new run to diff at all.
            db.ScreenRuns.Add(new ScreenRun
            {
                SnapshotId = snapshot.Id,
                CriteriaJson = ScreenCriteriaJson.Serialize(criteria),
                RunAt = DateTimeOffset.UtcNow,
                ResultCount = result.Rows.Count,
                DurationMs = 0,
                FromCache = true,
                SavedStrategyId = savedStrategyId,
                MemberSecuritiesJson = savedStrategyId is null
                    ? null
                    : JsonSerializer.Serialize(result.Rows.Select(r => new { r.Id, r.Ticker, r.Name })),
            });
            await db.SaveChangesAsync(ct);

            // The cached VALUE still carries the original run's timing. Without this, an API
            // caller sees the same stale DurationMs on every subsequent hit and has no way to
            // tell a genuinely fast response apart from one served entirely from memory.
            return result with { DurationMs = 0, FromCache = true };
        }

        return result;
    }

    private static string BuildKey(ScreenCriteria criteria, int snapshotId)
    {
        var json = ScreenCriteriaJson.Serialize(criteria);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"screen:{Convert.ToHexString(hash, 0, 16).ToLower(CultureInfo.InvariantCulture)}:{snapshotId}";
    }
}
