using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.MarketData.IndianApi;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Ingests fundamentals and corporate actions (PLAN.md §4.1, §4.3, §10 Phase 1).
///
/// Quota-aware by construction: one /stock call per security serves both, and the run stops
/// cleanly when the daily allowance is spent rather than failing mid-pass. Securities are
/// processed in priority order so the most important ones are covered first on any given day.
/// </summary>
public sealed class FundamentalsIngestionService(
    MarketEyeDbContext db,
    IndianApiClient client,
    ILogger<FundamentalsIngestionService> logger)
{
    /// <param name="symbols">
    /// Optional explicit tickers. Without it the service picks by priority, which is alphabetical
    /// within "never fetched" -- and that lands on micro-caps the provider may not cover, making a
    /// test run look like a mapping failure when it is really a coverage gap. Naming symbols keeps
    /// diagnosis cheap in a 500/day budget.
    /// </param>
    public async Task<FundamentalsIngestionReport> RunAsync(
        int maxSecurities, IReadOnlyList<string>? symbols, CancellationToken ct)
    {
        var report = new FundamentalsIngestionReport();

        var remaining = await client.RemainingCallsAsync(ct);
        if (remaining == 0)
        {
            logger.LogWarning("No provider calls remaining today.");
            report.QuotaExhausted = true;
            return report;
        }

        var take = Math.Min(maxSecurities, remaining);

        // Priority order: securities never fetched come first, then the least recently refreshed.
        // Quarterly results change four times a year, so re-fetching everything nightly would
        // spend the quota on unchanged data (ADR-0005).
        var query = db.Securities.Where(s => s.IsActive);

        // Ordered in both branches: Take without OrderBy gives an unpredictable set, which in a
        // quota-limited run means a different arbitrary slice each night.
        query = symbols is { Count: > 0 }
            ? query.Where(s => symbols.Contains(s.Ticker)).OrderBy(s => s.Ticker)
            : query
                .OrderBy(s => db.Fundamentals.Any(f => f.SecurityId == s.Id) ? 1 : 0)
                .ThenBy(s => s.Ticker);

        var securities = await query
            .Take(take)
            .Select(s => new { s.Id, s.Ticker })
            .ToListAsync(ct);

        logger.LogInformation("Fetching fundamentals for: {Tickers}",
            string.Join(", ", securities.Select(s => s.Ticker)));

        foreach (var security in securities)
        {
            if (ct.IsCancellationRequested) break;

            JsonDocumentHolder? holder = null;
            try
            {
                var doc = await client.GetStockAsync(security.Ticker, ct);
                if (doc is null)
                {
                    // Null means either quota exhausted or not covered. Check which, because one
                    // means stop and the other means continue.
                    if (await client.RemainingCallsAsync(ct) == 0)
                    {
                        report.QuotaExhausted = true;
                        logger.LogInformation("Stopping: daily quota reached after {N} securities",
                            report.SecuritiesProcessed);
                        break;
                    }
                    report.NotFound++;
                    continue;
                }

                holder = new JsonDocumentHolder(doc);

                var actions = IndianApiParser.ParseCorporateActions(doc, security.Id);
                report.CorporateActionsWritten += await UpsertActionsAsync(actions, ct);

                var fundamentals = IndianApiParser.ParseFundamentals(doc, security.Id);
                report.FundamentalsWritten += await UpsertFundamentalsAsync(fundamentals, ct);

                report.SecuritiesProcessed++;
            }
            catch (Exception ex)
            {
                // One bad security must not end a quota-limited run; the calls already spent
                // cannot be reclaimed.
                logger.LogError(ex, "Failed ingesting {Ticker}", security.Ticker);
                report.Failed++;

                // Critical: a failed SaveChanges leaves the shared DbContext holding the entities
                // that could not be written. Every later operation on this context -- including the
                // budget's own save -- then retries them and throws again, so ONE bad security
                // fails the whole remaining run without making a single further API call.
                //
                // Observed exactly that: 5 securities "failed" while only 1 call was consumed.
                db.ChangeTracker.Clear();
            }
            finally
            {
                holder?.Dispose();
            }
        }

        report.CallsRemaining = await client.RemainingCallsAsync(ct);
        return report;
    }

    /// <summary>
    /// Inserts only actions that are not already present. The unique index on
    /// (SecurityId, EffectiveDate, ActionType) is the backstop; this avoids relying on an
    /// exception for control flow, and a duplicated split would adjust prices twice.
    /// </summary>
    private async Task<int> UpsertActionsAsync(List<CorporateAction> actions, CancellationToken ct)
    {
        if (actions.Count == 0) return 0;

        var securityId = actions[0].SecurityId;
        var existing = await db.CorporateActions
            .Where(a => a.SecurityId == securityId)
            .Select(a => new { a.EffectiveDate, a.ActionType })
            .ToListAsync(ct);

        var known = existing.Select(e => (e.EffectiveDate, e.ActionType)).ToHashSet();

        var toAdd = actions
            .Where(a => known.Add((a.EffectiveDate, a.ActionType)))
            .ToList();

        if (toAdd.Count == 0) return 0;

        db.CorporateActions.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return toAdd.Count;
    }

    /// <summary>
    /// Writes fundamentals into the temporal table. Updating an existing period is exactly what
    /// §4.1 wants: SQL Server keeps the prior version in history, so a restatement stays readable
    /// via FOR SYSTEM_TIME AS OF and a backtest sees what was known then.
    /// </summary>
    private async Task<int> UpsertFundamentalsAsync(List<Fundamentals> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        var securityId = rows[0].SecurityId;
        var existing = await db.Fundamentals
            .Where(f => f.SecurityId == securityId)
            .ToDictionaryAsync(f => f.FiscalPeriodEnd, ct);

        var written = 0;
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.FiscalPeriodEnd, out var current))
            {
                if (current.Revenue == row.Revenue && current.NetIncome == row.NetIncome &&
                    current.TotalDebt == row.TotalDebt &&
                    current.ShareholdersEquity == row.ShareholdersEquity)
                {
                    // Unchanged. Writing anyway would add a spurious history row and make it look
                    // as though the company restated when it did not.
                    continue;
                }

                current.Revenue = row.Revenue;
                current.NetIncome = row.NetIncome;
                current.TotalDebt = row.TotalDebt;
                current.ShareholdersEquity = row.ShareholdersEquity;
                current.ReportedDate = row.ReportedDate;
                current.IsReportedDateEstimated = row.IsReportedDateEstimated;
            }
            else
            {
                db.Fundamentals.Add(row);
            }
            written++;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return written;
    }

    private sealed class JsonDocumentHolder(System.Text.Json.JsonDocument doc) : IDisposable
    {
        public void Dispose() => doc.Dispose();
    }
}

public sealed class FundamentalsIngestionReport
{
    public int SecuritiesProcessed { get; set; }
    public int FundamentalsWritten { get; set; }
    public int CorporateActionsWritten { get; set; }
    public int NotFound { get; set; }
    public int Failed { get; set; }
    public bool QuotaExhausted { get; set; }
    public int CallsRemaining { get; set; }
}
