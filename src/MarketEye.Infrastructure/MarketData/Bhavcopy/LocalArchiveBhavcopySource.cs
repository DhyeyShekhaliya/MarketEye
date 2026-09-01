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
        var mon = date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        var legacy = $"cm{date:dd}{mon}{date.Year}bhav";

        // Both naming schemes, both extensions, anywhere under the root.
        foreach (var pattern in new[] { $"*{yyyymmdd}*", $"{legacy}*" })
        {
            var match = Directory
                .EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".csv.zip", StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match;
        }
        return null;
    }
}
