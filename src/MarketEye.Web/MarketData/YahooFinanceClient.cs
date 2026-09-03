using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketEye.Web.MarketData;

/// <summary>
/// Reads a delayed quote and recent daily closes for one symbol from Yahoo Finance's public
/// (unofficial, undocumented) chart endpoint, for the homepage NIFTY 50 ticker.
///
/// This is deliberately isolated to the Web project and to this one page. Nothing else in
/// MarketEye reads from it: every screen, backtest, and alert still resolves against the app's
/// own sealed data snapshots (PLAN.md §4.5) and NSE bhavcopy ingestion (docs/adr/0005) -- the
/// system's actual point-in-time correctness guarantees do not depend on this endpoint staying up.
/// There is no SLA or rate-limit guarantee here, and the response shape can change without notice;
/// a failure here degrades to a friendly message on the homepage, never a broken screen or backtest.
/// </summary>
public sealed class YahooFinanceClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<YahooQuote?> GetDailyQuoteAsync(string symbol, CancellationToken ct)
    {
        var url = $"v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1mo&interval=1d";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<YahooChartResponse>(stream, JsonOptions, ct);
        var result = payload?.Chart?.Result?.FirstOrDefault();
        if (result?.Meta is null) return null;

        var timestamps = result.Timestamp ?? [];
        var quoteCloses = result.Indicators?.Quote?.FirstOrDefault()?.Close ?? [];
        var closes = new List<(DateOnly Date, decimal Close)>();
        for (var i = 0; i < timestamps.Count && i < quoteCloses.Count; i++)
        {
            if (quoteCloses[i] is not { } close) continue; // a market holiday inside the range comes back null
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime);
            closes.Add((date, (decimal)close));
        }

        var meta = result.Meta;

        // meta.previousClose comes back null often enough to matter (observed live). Falling back
        // straight to chartPreviousClose is wrong here: with range=1mo that field is the close from
        // a MONTH ago, not yesterday's -- silently mislabelling a stale monthly reference as "prev
        // close" would misstate the day's move. The second-to-last daily close in the series
        // actually IS yesterday's close, so prefer that over chartPreviousClose when available.
        var previousClose = meta.PreviousClose
            ?? (closes.Count >= 2 ? (double)closes[^2].Close : meta.ChartPreviousClose ?? 0);

        return new YahooQuote(
            Symbol: meta.Symbol ?? symbol,
            Currency: meta.Currency ?? "",
            Price: (decimal)(meta.RegularMarketPrice ?? 0),
            PreviousClose: (decimal)previousClose,
            DayHigh: meta.RegularMarketDayHigh is { } h ? (decimal)h : null,
            DayLow: meta.RegularMarketDayLow is { } l ? (decimal)l : null,
            AsOf: meta.RegularMarketTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : DateTimeOffset.UtcNow,
            RecentCloses: closes);
    }

    private sealed class YahooChartResponse
    {
        [JsonPropertyName("chart")] public YahooChart? Chart { get; set; }
    }

    private sealed class YahooChart
    {
        [JsonPropertyName("result")] public List<YahooResult>? Result { get; set; }
    }

    private sealed class YahooResult
    {
        [JsonPropertyName("meta")] public YahooMeta? Meta { get; set; }
        [JsonPropertyName("timestamp")] public List<long>? Timestamp { get; set; }
        [JsonPropertyName("indicators")] public YahooIndicators? Indicators { get; set; }
    }

    private sealed class YahooMeta
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("regularMarketPrice")] public double? RegularMarketPrice { get; set; }
        [JsonPropertyName("previousClose")] public double? PreviousClose { get; set; }
        [JsonPropertyName("chartPreviousClose")] public double? ChartPreviousClose { get; set; }
        [JsonPropertyName("regularMarketDayHigh")] public double? RegularMarketDayHigh { get; set; }
        [JsonPropertyName("regularMarketDayLow")] public double? RegularMarketDayLow { get; set; }
        [JsonPropertyName("regularMarketTime")] public long? RegularMarketTime { get; set; }
    }

    private sealed class YahooIndicators
    {
        [JsonPropertyName("quote")] public List<YahooQuoteSeries>? Quote { get; set; }
    }

    private sealed class YahooQuoteSeries
    {
        [JsonPropertyName("close")] public List<double?>? Close { get; set; }
    }
}

public sealed record YahooQuote(
    string Symbol,
    string Currency,
    decimal Price,
    decimal PreviousClose,
    decimal? DayHigh,
    decimal? DayLow,
    DateTimeOffset AsOf,
    List<(DateOnly Date, decimal Close)> RecentCloses);
