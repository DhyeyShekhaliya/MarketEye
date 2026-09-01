using System.Globalization;
using System.IO.Compression;

namespace MarketEye.Infrastructure.MarketData.Bhavcopy;

/// <summary>
/// Reads bhavcopies from a local directory — a cloned public archive mirror.
///
/// This is the backfill path. Fetching 1,250 files from NSE one at a time is the pattern that
/// gets an IP blocked partway through, leaving a half-filled database and no clean way to resume.
/// A mirror clone is one operation, resumable, and puts no load on the exchange.
///
/// Accepts plain .csv or zipped .csv.zip, and is tolerant about file naming because archive repos
/// do not agree on a convention.
/// </summary>
public sealed class LocalArchiveBhavcopySource(string rootDirectory) : IBhavcopySource
{
    // The archive is thousands of files across nested directories, and a backfill asks for ~1,250
    // dates. Walking the tree per lookup turns the backfill into directory-enumeration work
    // rather than ingestion work, so the listing is built once.
    private readonly Lazy<IReadOnlyList<string>> _files = new(() =>
        Directory.Exists(rootDirectory)
            ? Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".csv.zip", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : []);

    public async Task<string?> GetCsvAsync(DateOnly date, CancellationToken ct)
    {
        var path = FindFile(date);
        if (path is null) return null;

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await using var fs = File.OpenRead(path);
            return await NseBhavcopyClient.ExtractCsvAsync(fs, ct);
        }
        return await File.ReadAllTextAsync(path, ct);
    }

    private string? FindFile(DateOnly date)
    {
        if (!Directory.Exists(rootDirectory)) return null;

        var yyyymmdd = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        // NSE's sec_bhavdata_full files are named DDMMYYYY, not YYYYMMDD. Searching only for the
        // ISO form silently finds nothing and looks exactly like a market holiday.
        var ddmmyyyy = date.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
        var mon = date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        var legacy = $"cm{date:dd}{mon}{date.Year}bhav";

        // Legacy FIRST, deliberately. Where both layouts exist for a date, the legacy file carries
        // an ISIN and sec_bhavdata_full does not — and ISIN is what §4.4 keys identity on. Trying
        // the newer name first silently discards the better data.
        foreach (var token in new[] { legacy, $"_{ddmmyyyy}", yyyymmdd })
        {
            var match = _files.Value.FirstOrDefault(f =>
                Path.GetFileName(f).Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }
}
