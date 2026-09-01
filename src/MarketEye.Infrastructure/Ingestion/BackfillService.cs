using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Application.CorporateActions;
using MarketEye.Application.Indicators;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Historical backfill (PLAN.md §10 Phase 1, §12).
///
/// Deliberately NOT the nightly path. The nightly job ingests one day and recomputes the
/// securities it touched; doing that for every day of a five-year backfill is O(days²) — each day
/// re-reads and re-derives every security's entire history — and simply does not finish.
///
/// This runs in two passes instead: bulk-load every bar first, then derive indicators once per
/// security at the end. Same result, linear instead of quadratic.
/// </summary>
public sealed class BackfillService(
    MarketEyeDbContext db,
    IBhavcopySource source,
    BhavcopyParser parser,
    IsinResolver isins,
    PriceBarBulkWriter priceWriter,
    IndicatorBulkWriter indicatorWriter,
    SnapshotLifecycle snapshots,
    ILogger<BackfillService> logger)
{
    public async Task<BackfillReport> RunAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var report = new BackfillReport { From = from, To = to };

        // Pass 0: learn symbol → ISIN from any older ISIN-bearing files in the archive, so
        // securities keep a stable identity where one is recoverable (§4.4).
        await LearnIsinsAsync(from, ct);
        report.IsinsMapped = isins.MappedCount;

        // Pass 1: bars only. No derived values, no per-day recompute.
        var securityCache = await db.Securities
            .ToDictionaryAsync(s => s.ProviderSecurityId, s => s.Id, StringComparer.Ordinal, ct);

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var csv = await source.GetCsvAsync(d, ct);
            if (csv is null) { report.DaysMissing++; continue; }

            IReadOnlyList<BhavcopyRow> rows;
            try
            {
                using var reader = new StringReader(csv);
                rows = parser.Parse(reader);
            }
            catch (FormatException ex)
            {
                logger.LogError(ex, "Unparseable bhavcopy for {Date}", d);
                report.DaysFailed++;
                continue;
            }

            if (rows.Count == 0) { report.DaysMissing++; continue; }

            await EnsureSecuritiesAsync(rows, d, securityCache, ct);

            var bars = new List<PriceBar>(rows.Count);
            foreach (var r in rows)
            {
                if (!securityCache.TryGetValue(isins.Resolve(r), out var id)) continue;
                bars.Add(new PriceBar
                {
                    SecurityId = id, Date = r.Date,
                    Open = r.Open, High = r.High, Low = r.Low, Close = r.Close,
                    AdjClose = r.Close,   // derived in pass 2
                    Volume = r.Volume,
                    IsCircuitLocked = false,
                });
            }

            report.BarsWritten += await priceWriter.WriteAsync(bars, ct);
            report.DaysIngested++;

            if (report.DaysIngested % 50 == 0)
            {
                logger.LogInformation(
                    "Backfill {Date}: {Days} days, {Bars} bars, {Elapsed}",
                    d, report.DaysIngested, report.BarsWritten, sw.Elapsed);
            }
        }

        report.SecuritiesCreated = securityCache.Count;
        report.SyntheticIds = isins.FallbackCount;

        // Pass 2: derive once.
        report.IndicatorRows = await RecomputeAllAsync(ct);

        // Seal one snapshot for the final date, so screens have something to resolve against.
        if (report.BarsWritten > 0)
        {
            var snapshot = await snapshots.OpenAsync(to, "nse-bhavcopy-archive/1", ct);
            await snapshots.SealAsync(snapshot.Id, report.BarsWritten, 0, ct);
            report.SnapshotId = snapshot.Id;
        }

        report.Elapsed = sw.Elapsed;
        return report;
    }

    /// <summary>
    /// Scans older archive files for ISIN-bearing layouts. Cheap relative to the backfill and it
    /// is the only way securities in this window keep a stable identity.
    /// </summary>
    private async Task LearnIsinsAsync(DateOnly before, CancellationToken ct)
    {
        var learned = 0;
        for (var d = before.AddDays(-1); d > before.AddYears(-3) && learned < 40; d = d.AddDays(-1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var csv = await source.GetCsvAsync(d, ct);
            if (csv is null) continue;

            using var reader = new StringReader(csv);
            IReadOnlyList<BhavcopyRow> rows;
            try { rows = parser.Parse(reader); } catch (FormatException) { continue; }

            if (rows.Count == 0 || rows[0].Isin.Length == 0) continue;

            isins.Learn(rows);
            learned++;
        }
        logger.LogInformation(
            "ISIN map built from {Files} archive files: {Count} symbols", learned, isins.MappedCount);
    }

    private async Task EnsureSecuritiesAsync(
        IReadOnlyList<BhavcopyRow> rows, DateOnly date,
        Dictionary<string, int> cache, CancellationToken ct)
    {
        List<Security>? toAdd = null;
        foreach (var r in rows)
        {
            var id = isins.Resolve(r);
            if (cache.ContainsKey(id)) continue;

            toAdd ??= [];
            if (toAdd.Any(s => s.ProviderSecurityId == id)) continue;

            toAdd.Add(new Security
            {
                Ticker = r.Symbol, ProviderSecurityId = id, Name = r.Symbol,
                Exchange = "NSE", IsActive = true,
            });
        }

        if (toAdd is null) return;

        db.Securities.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        foreach (var s in toAdd) cache[s.ProviderSecurityId] = s.Id;
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// One derivation pass over every security. Streams per security so peak memory stays bounded
    /// by the largest single history rather than by the whole dataset.
    /// </summary>
    private async Task<long> RecomputeAllAsync(CancellationToken ct)
    {
        var connectionString = db.Database.GetConnectionString()!;
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var securityIds = (await conn.QueryAsync<int>(
            "SELECT DISTINCT SecurityId FROM dbo.PriceBars")).ToList();

        long written = 0;
        var batch = new List<IndicatorSet>(50_000);

        foreach (var securityId in securityIds)
        {
            var bars = (await conn.QueryAsync<PriceBar>(
                "SELECT SecurityId, Date, [Open], High, Low, [Close], AdjClose, Volume, IsCircuitLocked " +
                "FROM dbo.PriceBars WHERE SecurityId = @securityId ORDER BY Date",
                new { securityId })).ToList();

            if (bars.Count == 0) continue;

            var actions = await db.CorporateActions.AsNoTracking()
                .Where(a => a.SecurityId == securityId).ToListAsync(ct);

            // AdjClose first: indicators derive from it, and computing them off raw Close would
            // put a false spike at every split and bonus (§4.4).
            var adjusted = PriceAdjuster.AdjustedCloses(bars, actions);

            var highs = bars.Select(b => b.High).ToList();
            var lows = bars.Select(b => b.Low).ToList();

            var sma50 = TechnicalIndicators.Sma(adjusted, 50);
            var sma200 = TechnicalIndicators.Sma(adjusted, 200);
            var rsi14 = TechnicalIndicators.Rsi(adjusted, 14);
            var (macd, signal) = TechnicalIndicators.Macd(adjusted);
            var atr14 = TechnicalIndicators.Atr(highs, lows, adjusted, 14);
            var vol30 = TechnicalIndicators.RealisedVolatility(adjusted, 30);

            for (var i = 0; i < bars.Count; i++)
            {
                batch.Add(new IndicatorSet
                {
                    SecurityId = securityId, Date = bars[i].Date,
                    Sma50 = sma50[i], Sma200 = sma200[i], Rsi14 = rsi14[i],
                    Macd = macd[i], MacdSignal = signal[i], Atr14 = atr14[i], Vol30 = vol30[i],
                });
            }

            if (batch.Count >= 40_000)
            {
                written += await indicatorWriter.WriteAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0) written += await indicatorWriter.WriteAsync(batch, ct);

        logger.LogInformation("Recomputed indicators for {Securities} securities, {Rows} rows",
            securityIds.Count, written);
        return written;
    }
}

public sealed class BackfillReport
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public int DaysIngested { get; set; }
    public int DaysMissing { get; set; }
    public int DaysFailed { get; set; }
    public long BarsWritten { get; set; }
    public long IndicatorRows { get; set; }
    public int SecuritiesCreated { get; set; }
    public int IsinsMapped { get; set; }
    public int SyntheticIds { get; set; }
    public int? SnapshotId { get; set; }
    public TimeSpan Elapsed { get; set; }
}
