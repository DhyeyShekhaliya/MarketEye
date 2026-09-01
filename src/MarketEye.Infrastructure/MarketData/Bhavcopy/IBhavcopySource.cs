namespace MarketEye.Infrastructure.MarketData.Bhavcopy;

/// <summary>
/// Supplies one trading day's bhavcopy CSV.
///
/// Two implementations exist because backfill and nightly ingest have different constraints. A
/// five-year backfill is ~1,250 files and scraping that from NSE invites a block; the nightly job
/// is one file and can go direct. Both feed the same parser.
/// </summary>
public interface IBhavcopySource
{
    /// <summary>
    /// Returns the CSV text for <paramref name="date"/>, or null when no file exists — which is
    /// the normal answer for a weekend or an exchange holiday and must not be treated as failure.
    /// </summary>
    Task<string?> GetCsvAsync(DateOnly date, CancellationToken ct);
}
