using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Application.CorporateActions;
using MarketEye.Application.Indicators;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Ingestion.Jobs;

/// <summary>
/// One night's ingestion (PLAN.md §10 Phase 1, §4.5).
///
/// Deliberately a plain class, not a BackgroundService. App Service F1 has no Always On, so an
/// in-process timer never fires; the trigger is an external cron calling a protected endpoint
/// (`docs/adr/0006`). Keeping the logic here means moving back to a hosted service later is a
/// wiring change, not a rewrite.
///
/// Order matters: bars, then corporate actions, then adjusted closes, then indicators. Indicators
/// derive from AdjClose, so computing them before adjustment would bake a split-day spike into
/// every one of them.
/// </summary>
public sealed class DailyIngestionJob(
    MarketEyeDbContext db,
    PriceBarBulkWriter bulkWriter,
    SnapshotLifecycle snapshots,
    ILogger<DailyIngestionJob> logger)
{
    public async Task<IngestionResult> RunAsync(
        DateOnly tradingDate,
        IReadOnlyList<PriceBar> bars,
        string providerVersion,
        CancellationToken ct)
    {
        var run = new IngestionRun
        {
            Source = providerVersion,
            StartedAt = DateTimeOffset.UtcNow,
            Status = IngestionStatus.Running,
        };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        DataSnapshot? snapshot = null;
        try
        {
            snapshot = await snapshots.OpenAsync(tradingDate, providerVersion, ct);
            run.SnapshotId = snapshot.Id;

            var written = await bulkWriter.WriteAsync(bars, ct);
            logger.LogInformation("Wrote {Count} price bars for {Date}", written, tradingDate);

            await RecomputeDerivedAsync(bars.Select(b => b.SecurityId).Distinct().ToList(), ct);

            // Sealing last is the point: until this line, nothing reads any of it (§4.5).
            await snapshots.SealAsync(snapshot.Id, written, 0, ct);

            run.Status = IngestionStatus.Succeeded;
            run.RecordsWritten = written;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return new IngestionResult(true, written, snapshot.Id, null);
        }
        catch (Exception ex)
        {
            // §10 Phase 1 requires failure capture, not just a failure count. The message is what
            // makes a 3am failure diagnosable the next morning.
            logger.LogError(ex, "Ingestion failed for {Date}", tradingDate);

            run.Status = IngestionStatus.Failed;
            run.Error = ex.ToString();
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (snapshot is not null) await snapshots.AbandonAsync(snapshot.Id, ct);

            return new IngestionResult(false, 0, snapshot?.Id, ex.Message);
        }
    }

    /// <summary>
    /// Recomputes adjusted closes and indicators for the affected securities.
    ///
    /// `docs/adr/0006` makes this the one workload that must stay incremental: a nightly full
    /// recompute across the whole universe is what would exhaust App Service F1's 60 CPU-minute
    /// daily quota. Only securities touched by this run are recomputed.
    /// </summary>
    private async Task RecomputeDerivedAsync(IReadOnlyList<int> securityIds, CancellationToken ct)
    {
        foreach (var securityId in securityIds)
        {
            var bars = await db.PriceBars
                .Where(b => b.SecurityId == securityId)
                .OrderBy(b => b.Date)
                .ToListAsync(ct);

            if (bars.Count == 0) continue;

            var actions = await db.CorporateActions
                .Where(a => a.SecurityId == securityId)
                .ToListAsync(ct);

            var adjusted = PriceAdjuster.AdjustedCloses(bars, actions);
            for (var i = 0; i < bars.Count; i++) bars[i].AdjClose = adjusted[i];

            // Indicators derive from AdjClose. Using raw Close would put a false spike at every
            // split and bonus (§4.4).
            var closes = adjusted;
            var highs = bars.Select(b => b.High).ToList();
            var lows = bars.Select(b => b.Low).ToList();

            var sma50 = TechnicalIndicators.Sma(closes, 50);
            var sma200 = TechnicalIndicators.Sma(closes, 200);
            var rsi14 = TechnicalIndicators.Rsi(closes, 14);
            var (macd, signal) = TechnicalIndicators.Macd(closes);
            var atr14 = TechnicalIndicators.Atr(highs, lows, closes, 14);
            var vol30 = TechnicalIndicators.RealisedVolatility(closes, 30);

            var existing = await db.Indicators
                .Where(i => i.SecurityId == securityId)
                .ToDictionaryAsync(i => i.Date, ct);

            for (var i = 0; i < bars.Count; i++)
            {
                if (!existing.TryGetValue(bars[i].Date, out var row))
                {
                    row = new IndicatorSet { SecurityId = securityId, Date = bars[i].Date };
                    db.Indicators.Add(row);
                }
                row.Sma50 = sma50[i];
                row.Sma200 = sma200[i];
                row.Rsi14 = rsi14[i];
                row.Macd = macd[i];
                row.MacdSignal = signal[i];
                row.Atr14 = atr14[i];
                row.Vol30 = vol30[i];
            }
            await db.SaveChangesAsync(ct);
        }
    }
}

public sealed record IngestionResult(bool Succeeded, int RowsWritten, int? SnapshotId, string? Error);
