using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;

namespace MarketEye.Infrastructure.MarketData.Bhavcopy;

/// <summary>
/// Downloads a bhavcopy directly from NSE (PLAN.md §10 Phase 1: rate limiting, backoff,
/// idempotent re-runs).
///
/// NSE returns 403 to plain HTTP clients. Three things are required and none of them are optional:
/// a browser-like User-Agent, a Referer, and session cookies obtained by requesting the homepage
/// first. Requests are also rate limited — the community consensus is ~3/second before NSE starts
/// refusing, and this client is deliberately slower than that.
///
/// Intended for the NIGHTLY job, which fetches one file. For a five-year backfill use
/// <see cref="LocalArchiveBhavcopySource"/> against a cloned mirror instead: 1,250 sequential
/// scrapes is exactly the pattern that gets an IP blocked.
/// </summary>
public sealed class NseBhavcopyClient(
    HttpClient http,
    ILogger<NseBhavcopyClient> logger) : IBhavcopySource, IDisposable
{
    // NSE moved to the UDiFF layout on 8 July 2024. Before that date only the legacy archive path
    // exists; after it, only the new one. A backfill spanning the boundary needs both.
    private static readonly DateOnly UdiffCutover = new(2024, 7, 8);

    private readonly RateLimiter _limiter = new FixedWindowRateLimiter(new()
    {
        PermitLimit = 2,
        Window = TimeSpan.FromSeconds(1),
        QueueLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    private bool _cookiesPrimed;

    public async Task<string?> GetCsvAsync(DateOnly date, CancellationToken ct)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // Not an error. Asking NSE for a weekend file wastes a request against the rate limit.
            return null;
        }

        await PrimeCookiesAsync(ct);

        var url = BuildUrl(date);
        using var lease = await _limiter.AcquireAsync(1, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://www.nseindia.com/all-reports");

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // An exchange holiday. Distinct from a failure, and the caller must not seal a
            // snapshot for it.
            logger.LogInformation("No bhavcopy for {Date} (holiday or not yet published)", date);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Cookies expire. Re-prime once and let the resilience policy retry rather than
            // failing the whole night on a stale session.
            _cookiesPrimed = false;
            throw new HttpRequestException(
                $"NSE returned 403 for {date:yyyy-MM-dd}. Session cookies were rejected; " +
                "they will be re-primed on retry.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ExtractCsvAsync(stream, ct);
    }

    /// <summary>
    /// NSE issues session cookies on the homepage and rejects archive requests without them.
    /// This is the single most common reason a working scraper suddenly returns 403.
    /// </summary>
    private async Task PrimeCookiesAsync(CancellationToken ct)
    {
        if (_cookiesPrimed) return;

        using var lease = await _limiter.AcquireAsync(1, ct);
        using var response = await http.GetAsync("https://www.nseindia.com/", ct);
        response.EnsureSuccessStatusCode();

        _cookiesPrimed = true;
        logger.LogDebug("NSE session cookies primed");
    }

    private static string BuildUrl(DateOnly date)
    {
        if (date >= UdiffCutover)
        {
            var stamp = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return $"https://nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_{stamp}_F_0000.csv.zip";
        }

        var year = date.Year.ToString(CultureInfo.InvariantCulture);
        var mon = date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        var day = date.ToString("dd", CultureInfo.InvariantCulture);
        return $"https://archives.nseindia.com/content/historical/EQUITIES/{year}/{mon}/cm{day}{mon}{year}bhav.csv.zip";
    }

    /// <summary>Bhavcopies ship as a zip containing a single CSV.</summary>
    internal static async Task<string?> ExtractCsvAsync(Stream zipStream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await zipStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (entry is null) return null;

        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return await reader.ReadToEndAsync(ct);
    }

    public void Dispose() => _limiter.Dispose();
}
