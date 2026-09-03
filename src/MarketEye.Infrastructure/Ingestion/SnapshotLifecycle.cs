using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Write-then-seal lifecycle for <see cref="DataSnapshot"/> (PLAN.md §4.5).
///
/// Ingestion opens a snapshot, writes into it, and seals it only on success. Queries read sealed
/// snapshots exclusively, which gives four properties at once: results are reproducible forever, a
/// difference between two days is a diff rather than a guess, the result cache gets a free key,
/// and a half-finished nightly job leaves something nothing will ever read.
///
/// The last one is the reason sealing is a separate step rather than a flag set at creation.
/// </summary>
public sealed class SnapshotLifecycle(MarketEyeDbContext db)
{
    /// <summary>Opens an unsealed snapshot. Nothing reads it until <see cref="SealAsync"/>.</summary>
    public async Task<DataSnapshot> OpenAsync(
        DateOnly asOfDate, string providerVersion, CancellationToken ct)
    {
        var snapshot = new DataSnapshot
        {
            AsOfDate = asOfDate,
            CreatedAt = DateTimeOffset.UtcNow,
            SealedAt = null,
            ProviderVersion = providerVersion,
        };

        db.DataSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    /// <summary>
    /// Seals a snapshot, recording the row counts §4.5 asks for.
    ///
    /// Refuses to seal an empty snapshot. A market holiday and a silently failed download both
    /// produce zero rows, and sealing the second one would publish a day on which every security
    /// appears to have vanished.
    /// </summary>
    public async Task SealAsync(int snapshotId, long priceRows, long fundamentalRows, CancellationToken ct)
    {
        var snapshot = await db.DataSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
                       ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");

        if (snapshot.SealedAt is not null)
        {
            throw new InvalidOperationException(
                $"Snapshot {snapshotId} is already sealed. Sealed snapshots are immutable (§4.5).");
        }

        if (priceRows == 0)
        {
            throw new InvalidOperationException(
                $"Refusing to seal snapshot {snapshotId} with zero price rows. A silent download " +
                "failure is indistinguishable from a market holiday at this point, and sealing " +
                "would publish a day where every security appears to have vanished.");
        }

        snapshot.PriceRowCount = priceRows;
        snapshot.FundamentalRowCount = fundamentalRows;
        snapshot.SealedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The snapshot a screen should resolve against: the newest SEALED one at or before
    /// <paramref name="asOf"/>. Unsealed snapshots are invisible here by design.
    /// </summary>
    public Task<DataSnapshot?> LatestSealedAsync(DateOnly asOf, CancellationToken ct) =>
        db.DataSnapshots
            .Where(s => s.SealedAt != null && s.AsOfDate <= asOf)
            .OrderByDescending(s => s.AsOfDate)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Abandons a failed snapshot. Unsealed rows stay for post-mortem rather than vanishing.</summary>
    public async Task AbandonAsync(int snapshotId, CancellationToken ct)
    {
        var snapshot = await db.DataSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId, ct);
        if (snapshot is null || snapshot.SealedAt is not null) return;

        snapshot.ProviderVersion = $"{snapshot.ProviderVersion} (abandoned)";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seals one snapshot per date already present in <c>PriceBars</c> within [<paramref name="from"/>,
    /// <paramref name="to"/>] that does not already have a sealed snapshot for that exact date.
    ///
    /// <see cref="LatestSealedAsync"/> can only resolve a date at or before a sealed snapshot's own
    /// <c>AsOfDate</c> — so a screen or backtest run "as of" a historical date needs a snapshot
    /// sealed AT that date, not just bars sitting in the table. <see cref="BackfillService"/>
    /// historically sealed only one snapshot for its whole range (bars bulk-load in one pass,
    /// deliberately not per-day, to stay linear rather than quadratic — see its own doc comment),
    /// which left every earlier date in a backfilled range unresolvable for point-in-time reads.
    /// This method is the fix, and is also what makes an already-backfilled range usable
    /// retroactively without re-fetching or re-parsing a single bhavcopy file — it reads only the
    /// bar dates and counts already sitting in <c>PriceBars</c>.
    ///
    /// Idempotent: a date that already has a sealed snapshot is left alone, so re-running this over
    /// an overlapping range (a fresh nightly seal landing inside an old backfill window, say) never
    /// creates a duplicate.
    /// </summary>
    public async Task<int> SealHistoricalSnapshotsAsync(
        DateOnly from, DateOnly to, string providerVersion, CancellationToken ct)
    {
        var connectionString = db.Database.GetConnectionString()!;
        await using var conn = new SqlConnection(connectionString);

        // [RowCount] is bracket-quoted: unquoted, SQL Server parses ROWCOUNT as the SET ROWCOUNT
        // keyword rather than an identifier, and the query fails with a syntax error right next to
        // a token that looks perfectly innocent.
        var candidates = (await conn.QueryAsync<DateRowCount>(new CommandDefinition("""
            SELECT [Date], COUNT(*) AS [RowCount]
            FROM dbo.PriceBars
            WHERE [Date] BETWEEN @from AND @to
            GROUP BY [Date]
            ORDER BY [Date];
            """, new { from, to }, cancellationToken: ct))).ToList();

        if (candidates.Count == 0) return 0;

        var alreadySealed = (await conn.QueryAsync<DateOnly>(new CommandDefinition("""
            SELECT DISTINCT AsOfDate FROM dbo.DataSnapshots
            WHERE AsOfDate BETWEEN @from AND @to AND SealedAt IS NOT NULL;
            """, new { from, to }, cancellationToken: ct))).ToHashSet();

        var sealedCount = 0;
        foreach (var candidate in candidates)
        {
            if (alreadySealed.Contains(candidate.Date)) continue;

            var snapshot = await OpenAsync(candidate.Date, providerVersion, ct);
            await SealAsync(snapshot.Id, candidate.RowCount, fundamentalRows: 0, ct);
            sealedCount++;
        }

        return sealedCount;
    }

    // int, not long: Dapper matches a record's primary constructor against the reader's actual
    // column types, and COUNT(*) comes back as SQL int -- a declared `long` here fails to match
    // and Dapper throws "no parameterless constructor or matching signature" instead of widening.
    private sealed record DateRowCount(DateOnly Date, int RowCount);
}
