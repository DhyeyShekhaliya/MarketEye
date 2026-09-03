using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Application.Screening;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Screening;

namespace MarketEye.Ingestion.Jobs;

/// <summary>
/// Checks every saved strategy against the latest sealed snapshot and records entries/exits
/// (PLAN.md §10 Phase 4 "Alerts").
///
/// A plain class invoked by a protected HTTP endpoint, mirroring <see cref="DailyIngestionJob"/> --
/// App Service F1 has no Always On, so an in-process timer never fires (docs/adr/0006); an external
/// cron calls the endpoint instead, after nightly ingestion has sealed today's snapshot.
/// </summary>
public sealed class AlertCheckJob(
    MarketEyeDbContext db,
    CachedScreeningEngine engine,
    SnapshotLifecycle snapshots,
    ILogger<AlertCheckJob> logger)
{
    public async Task<AlertCheckResult> RunAsync(DateOnly asOfDate, CancellationToken ct)
    {
        var snapshot = await snapshots.LatestSealedAsync(asOfDate, ct);
        if (snapshot is null)
        {
            return new AlertCheckResult(false, 0, 0, "No sealed data snapshot exists at or before the given date.");
        }

        // Every saved strategy is checked, regardless of OwnerUserId -- there is no per-user
        // opt-in/opt-out yet (auth is still open per §14), matching how /api/strategies already
        // treats every row as visible to the single caller today.
        var strategies = await db.SavedStrategies.AsNoTracking().ToListAsync(ct);

        var strategiesChecked = 0;
        var eventsRaised = 0;

        foreach (var strategy in strategies)
        {
            try
            {
                var previousRun = await db.ScreenRuns.AsNoTracking()
                    .Where(r => r.SavedStrategyId == strategy.Id && r.MemberSecuritiesJson != null)
                    .OrderByDescending(r => r.RunAt)
                    .FirstOrDefaultAsync(ct);

                var criteria = ScreenCriteriaJson.Deserialize(strategy.CriteriaJson);
                await engine.RunAsync(criteria, snapshot, strategy.Id, ct);

                // engine.RunAsync just committed a new ScreenRun linked to this strategy (whether
                // it ran fresh or hit the cache -- both paths now write one, per
                // CachedScreeningEngine). Reading it back by id, rather than trusting a returned
                // reference, keeps this job correct regardless of which path fired.
                var newRun = await db.ScreenRuns.AsNoTracking()
                    .Where(r => r.SavedStrategyId == strategy.Id)
                    .OrderByDescending(r => r.Id)
                    .FirstAsync(ct);

                eventsRaised += await AlertDiffer.DiffAndRecordAsync(
                    db, strategy.Id, previousRun, newRun, snapshot.AsOfDate, ct);
                strategiesChecked++;
            }
            catch (InvalidOperationException ex)
            {
                // A vocabulary edit since this strategy was saved can make its stored criteria
                // invalid today (§5.1: that is an answer, not a server error) -- exactly the
                // tolerance /api/strategies/{name}/run already extends. One bad strategy must not
                // abort the batch for every other saved strategy.
                logger.LogWarning(ex, "Alert check skipped for strategy {Name}: {Message}", strategy.Name, ex.Message);
            }
        }

        return new AlertCheckResult(true, strategiesChecked, eventsRaised, null);
    }
}

public sealed record AlertCheckResult(bool Succeeded, int StrategiesChecked, int EventsRaised, string? Error);
