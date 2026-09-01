using System.Text.Json;
using Sift.Application.MarketData;

namespace Sift.Infrastructure.MarketData;

/// <summary>
/// Fixture-backed <see cref="IMarketDataProvider"/> (PLAN.md §10, Phase 0).
///
/// Reads committed JSON so the whole stack runs with no vendor account, no API key and
/// no network. It is not a mock: it returns the same shapes a real provider will, so the
/// ingestion code written in Phase 1 can be developed and tested against it first.
/// </summary>
public sealed class FixtureMarketDataProvider : IMarketDataProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    public FixtureMarketDataProvider()
        : this(Path.Combine(AppContext.BaseDirectory, "MarketData", "Fixtures")) { }

    public FixtureMarketDataProvider(string fixtureRoot) => _root = fixtureRoot;

    public string ProviderVersion => "fixture/1";

    public Task<IReadOnlyList<SecurityDto>> GetSecuritiesAsync(CancellationToken ct) =>
        ReadAsync<SecurityDto>("securities.json", ct);

    public async Task<IReadOnlyList<PriceBarDto>> GetPriceBarsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var all = await ReadAsync<PriceBarDto>($"prices.{providerSecurityId}.json", ct);
        return all.Where(b => b.Date >= from && b.Date <= to).OrderBy(b => b.Date).ToList();
    }

    public async Task<IReadOnlyList<FundamentalsDto>> GetFundamentalsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var all = await ReadAsync<FundamentalsDto>($"fundamentals.{providerSecurityId}.json", ct);
        // Filtered on ReportedDate, not FiscalPeriodEnd: the caller asks what the market
        // knew inside a window, which is a reporting-date question (§4.1).
        return all.Where(f => f.ReportedDate >= from && f.ReportedDate <= to)
                  .OrderBy(f => f.ReportedDate).ToList();
    }

    public async Task<IReadOnlyList<CorporateActionDto>> GetCorporateActionsAsync(
        string providerSecurityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var all = await ReadAsync<CorporateActionDto>($"actions.{providerSecurityId}.json", ct);
        return all.Where(a => a.EffectiveDate >= from && a.EffectiveDate <= to)
                  .OrderBy(a => a.EffectiveDate).ToList();
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string file, CancellationToken ct)
    {
        var path = Path.Combine(_root, file);
        if (!File.Exists(path)) return [];
        await using var s = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(s, Json, ct) ?? [];
    }
}
