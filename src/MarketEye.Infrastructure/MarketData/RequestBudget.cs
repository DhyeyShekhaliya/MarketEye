using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.MarketData;

/// <summary>
/// Enforces the provider's daily request quota (PLAN.md §12).
///
/// indianapi.in's free tier is 500 calls/day. With 3,481 securities in the database, a single
/// full fundamentals pass would take a week — which is the concrete reason the universe is scoped
/// to NIFTY 50 plus delisted members (ADR-0005) rather than the whole market.
///
/// The budget is checked BEFORE a call, not after. Discovering the quota is exhausted by receiving
/// a 429 wastes the call and, on a provider that counts rejected requests, digs the hole deeper.
/// </summary>
public sealed class RequestBudget(
    MarketEyeDbContext db,
    ILogger<RequestBudget> logger)
{
    /// <summary>
    /// Reserves <paramref name="count"/> calls if the quota allows. Returns false when it does
    /// not — callers must stop and resume tomorrow rather than retry.
    /// </summary>
    public async Task<bool> TryConsumeAsync(
        string provider, int dailyLimit, int count, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budget = await db.Set<ApiCallBudget>()
            .FirstOrDefaultAsync(b => b.Provider == provider && b.Date == today, ct);

        if (budget is null)
        {
            budget = new ApiCallBudget
            {
                Provider = provider, Date = today, CallsUsed = 0,
                DailyLimit = dailyLimit, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(budget);
        }

        if (budget.CallsUsed + count > dailyLimit)
        {
            logger.LogWarning(
                "{Provider} daily quota exhausted: {Used}/{Limit} used, {Requested} more requested. " +
                "Remaining work resumes after 00:00 UTC.",
                provider, budget.CallsUsed, dailyLimit, count);
            return false;
        }

        budget.CallsUsed += count;
        budget.DailyLimit = dailyLimit;
        budget.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Calls still available today. Zero means stop.</summary>
    public async Task<int> RemainingAsync(string provider, int dailyLimit, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var used = await db.Set<ApiCallBudget>()
            .Where(b => b.Provider == provider && b.Date == today)
            .Select(b => (int?)b.CallsUsed)
            .FirstOrDefaultAsync(ct) ?? 0;

        return Math.Max(0, dailyLimit - used);
    }
}
