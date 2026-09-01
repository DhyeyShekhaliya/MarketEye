using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MarketEye.Infrastructure.MarketData;

namespace MarketEye.Infrastructure.MarketData.IndianApi;

/// <summary>
/// Client for indianapi.in (PLAN.md §4.1, ADR-0005).
///
/// One <c>/stock</c> call returns fundamentals AND corporate actions, so it is fetched once and
/// parsed twice. Splitting them into two calls would double consumption of a 500/day quota for no
/// benefit — see the rate-limit section of ADR-0005.
///
/// Every call is reserved through <see cref="RequestBudget"/> BEFORE it is made. Discovering the
/// quota is exhausted by receiving a rejection has already spent the request.
/// </summary>
public sealed class IndianApiClient(
    HttpClient http,
    RequestBudget budget,
    IConfiguration config,
    ILogger<IndianApiClient> logger)
{
    public const string ProviderKey = "indianapi";

    private int DailyLimit => config.GetValue("Provider:IndianApi:DailyRequestLimit", 500);
    private string? ApiKey => config["Provider:IndianApi:ApiKey"];

    /// <summary>
    /// Fetches one security. Returns null when the daily quota is exhausted — the caller must stop
    /// and resume tomorrow rather than retry, or it will simply burn the next day's allowance too.
    /// </summary>
    public async Task<JsonDocument?> GetStockAsync(string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "Provider:IndianApi:ApiKey is not configured. Locally: " +
                "dotnet user-secrets set \"Provider:IndianApi:ApiKey\" \"<key>\" --project src/MarketEye.Api");
        }

        if (!await budget.TryConsumeAsync(ProviderKey, DailyLimit, 1, ct))
        {
            logger.LogWarning("Daily quota exhausted; skipping {Symbol}", symbol);
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/stock?name={Uri.EscapeDataString(symbol)}");
        request.Headers.Add("X-Api-Key", ApiKey);

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not every NSE symbol exists in the provider's coverage. A gap is normal and must not
            // abort a run over hundreds of securities.
            logger.LogInformation("{Symbol} not found at the provider", symbol);
            return null;
        }

        if ((int)response.StatusCode == 429)
        {
            // The provider disagrees with our accounting. Trust the provider and stop: continuing
            // would spend calls that are already being refused.
            throw new InvalidOperationException(
                $"Provider returned 429 for {symbol}. The daily quota is exhausted despite local " +
                "accounting saying otherwise; check ApiCallBudgets for drift.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public Task<int> RemainingCallsAsync(CancellationToken ct) =>
        budget.RemainingAsync(ProviderKey, DailyLimit, ct);
}
