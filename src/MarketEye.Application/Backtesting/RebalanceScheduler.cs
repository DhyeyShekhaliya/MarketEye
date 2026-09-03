using MarketEye.Domain.Backtesting;

namespace MarketEye.Application.Backtesting;

/// <summary>Pure date arithmetic: turns a date range + frequency into rebalance dates (§7).</summary>
public static class RebalanceScheduler
{
    public static IReadOnlyList<DateOnly> Dates(DateOnly start, DateOnly end, RebalanceFrequency freq)
    {
        if (start > end)
        {
            throw new ArgumentException(
                $"Start date {start:yyyy-MM-dd} must be on or before end date {end:yyyy-MM-dd}.");
        }

        var dates = new List<DateOnly> { start };
        var current = start;
        while (true)
        {
            var next = freq switch
            {
                RebalanceFrequency.Monthly => current.AddMonths(1),
                RebalanceFrequency.Quarterly => current.AddMonths(3),
                RebalanceFrequency.Annual => current.AddYears(1),
                _ => throw new ArgumentOutOfRangeException(nameof(freq), freq, "Unknown rebalance frequency."),
            };
            if (next > end) break;
            dates.Add(next);
            current = next;
        }
        return dates;
    }
}
