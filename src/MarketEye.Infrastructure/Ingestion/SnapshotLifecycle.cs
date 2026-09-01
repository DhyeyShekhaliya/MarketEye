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
}
