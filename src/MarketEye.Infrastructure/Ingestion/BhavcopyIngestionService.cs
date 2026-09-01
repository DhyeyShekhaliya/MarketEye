using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Turns one day's bhavcopy into securities and price bars (PLAN.md §4.3, §4.4).
///
/// Identity is the ISIN, never the ticker. §4.4 requires that a ticker change not create a second
/// Security row, and NSE puts the ISIN in the file — so the rule is enforceable rather than
/// aspirational. A symbol that changes against a known ISIN updates the existing row and records a
/// TickerChange corporate action.
/// </summary>
public sealed class BhavcopyIngestionService(
    MarketEyeDbContext db,
    IBhavcopySource source,
    BhavcopyParser parser,
    IsinResolver isins,
    ILogger<BhavcopyIngestionService> logger)
{
    /// <summary>
    /// Reads one trading day. Returns null when there is no file — a weekend or exchange holiday,
    /// which is a normal outcome and must not be confused with a failed download.
    /// </summary>
    public async Task<DayIngestion?> ReadDayAsync(DateOnly date, CancellationToken ct)
    {
        var csv = await source.GetCsvAsync(date, ct);
        if (csv is null) return null;

        using var reader = new StringReader(csv);
        var rows = parser.Parse(reader);
        if (rows.Count == 0)
        {
            logger.LogWarning("Bhavcopy for {Date} parsed to zero equity rows", date);
            return null;
        }

        var securityIds = await ReconcileSecuritiesAsync(rows, date, ct);

        var bars = new List<PriceBar>(rows.Count);
        foreach (var r in rows)
        {
            if (!securityIds.TryGetValue(isins.Resolve(r), out var securityId)) continue;

            bars.Add(new PriceBar
            {
                SecurityId = securityId,
                Date = r.Date,
                Open = r.Open,
                High = r.High,
                Low = r.Low,
                Close = r.Close,
                // Set to raw close here and recomputed once corporate actions are known. Writing
                // the raw value would be wrong if left alone, so the ingest job always follows
                // with a recompute -- never treat this as final (§4.4).
                AdjClose = r.Close,
                Volume = r.Volume,
                IsCircuitLocked = InferCircuitLock(r),
            });
        }

        return new DayIngestion(date, bars, rows.Count);
    }

    /// <summary>
    /// Heuristic, and labelled as one. A circuit-locked session usually trades at a single price,
    /// so open = high = low = close while the price has moved from the previous close. NSE does
    /// not publish a lock flag in the bhavcopy, so this is inference rather than fact.
    ///
    /// It matters because §7 must refuse fills on locked days. A false positive skips a trade the
    /// backtest could have made; a false negative claims one it could not. The second error
    /// flatters results, so the heuristic is deliberately biased toward the first.
    /// </summary>
    private static bool InferCircuitLock(BhavcopyRow r)
    {
        if (r.PreviousClose is not { } prev || prev <= 0) return false;
        if (r.High != r.Low || r.Open != r.Close || r.High != r.Close) return false;

        var move = Math.Abs((r.Close - prev) / prev);
        return move >= 0.019m;   // just under the common 2% band, before 5/10/20% bands
    }

    /// <summary>
    /// Upserts securities keyed on ISIN, recording ticker changes rather than duplicating rows.
    /// </summary>
    private async Task<Dictionary<string, int>> ReconcileSecuritiesAsync(
        IReadOnlyList<BhavcopyRow> rows, DateOnly date, CancellationToken ct)
    {
        // Identity comes from the resolver, not the row: the sec_bhavdata_full layout carries no
        // ISIN, so an ISIN-only path silently produces zero securities and zero bars (§4.4).
        var ids = rows.Select(r => isins.Resolve(r)).Distinct().ToList();

        var existing = await db.Securities
            .Where(s => ids.Contains(s.ProviderSecurityId))
            .ToDictionaryAsync(s => s.ProviderSecurityId, ct);

        // Also match on ticker. This path runs without a learned ISIN map, so it resolves symbols
        // to synthetic ids -- and would otherwise create a second row for a company the backfill
        // already stored under its real ISIN, splitting one price history in two.
        var symbols = rows.Select(r => r.Symbol).Distinct().ToList();
        var byTicker = await db.Securities
            .Where(s => symbols.Contains(s.Ticker))
            .ToDictionaryAsync(s => s.Ticker, StringComparer.Ordinal, ct);

        foreach (var kv in byTicker)
        {
            existing.TryAdd(kv.Value.ProviderSecurityId, kv.Value);
        }

        foreach (var r in rows)
        {
            var providerId = isins.Resolve(r);
            if (providerId.Length == 0) continue;

            // Prefer an existing row for this ticker over creating one under a different identity.
            if (!existing.TryGetValue(providerId, out var byId) && byTicker.TryGetValue(r.Symbol, out var byName))
            {
                existing[providerId] = byName;
            }

            if (existing.TryGetValue(providerId, out var security))
            {
                if (!string.Equals(security.Ticker, r.Symbol, StringComparison.Ordinal))
                {
                    // §4.4: same company, new symbol. One row, plus an auditable record of when
                    // it changed -- otherwise a five-year price series silently splits in two.
                    logger.LogInformation(
                        "Ticker change on {Id}: {Old} -> {New}", providerId, security.Ticker, r.Symbol);

                    db.CorporateActions.Add(new CorporateAction
                    {
                        SecurityId = security.Id,
                        EffectiveDate = date,
                        ActionType = CorporateActionType.TickerChange,
                        NewTicker = r.Symbol,
                        RawDescription = $"Symbol changed from {security.Ticker} to {r.Symbol}",
                    });

                    security.Ticker = r.Symbol;
                }

                // Appearing in today's file means it is trading, so a previous delisting was
                // either wrong or reversed. Relistings happen; leaving IsActive false would drop
                // the security from every future screen.
                if (!security.IsActive)
                {
                    security.IsActive = true;
                    security.DelistedDate = null;
                    security.DelistingReason = null;
                }
                continue;
            }

            var created = new Security
            {
                Ticker = r.Symbol,
                ProviderSecurityId = providerId,
                // The bhavcopy carries no company name. Populated later from the fundamentals
                // provider; the ticker is a usable placeholder and beats inventing one.
                Name = r.Symbol,
                Exchange = "NSE",
                IsActive = true,
            };
            db.Securities.Add(created);
            existing[providerId] = created;
        }

        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    }
}

public sealed record DayIngestion(DateOnly Date, IReadOnlyList<PriceBar> Bars, int RowsParsed);
