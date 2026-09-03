namespace MarketEye.Application.Screening;

/// <summary>
/// Pure set-diff for two consecutive screen runs' matched securities (PLAN.md §10 Phase 4
/// "Alerts"). No database dependency, mirroring how <c>TechnicalIndicators</c> and
/// <c>BacktestMetricsCalculator</c> keep the math testable in isolation from the orchestration that
/// reads and writes it (<c>AlertDiffer</c> in <c>MarketEye.Infrastructure</c>).
/// </summary>
public static class AlertSetDiffer
{
    public sealed record Member(int SecurityId, string Ticker);

    public sealed record DiffResult(IReadOnlyList<Member> Entered, IReadOnlyList<Member> Exited);

    /// <summary>
    /// Diffs on <see cref="Member.SecurityId"/>, never the ticker string: a ticker can be reissued
    /// to a different company after a delisting (§4.4's provider-id-not-ticker precedent), so
    /// diffing on ticker could misattribute an exit/entry pair across a ticker reuse.
    /// </summary>
    public static DiffResult Diff(IReadOnlyList<Member> previous, IReadOnlyList<Member> current)
    {
        var previousIds = previous.Select(m => m.SecurityId).ToHashSet();
        var currentIds = current.Select(m => m.SecurityId).ToHashSet();
        var previousById = previous.ToDictionary(m => m.SecurityId);
        var currentById = current.ToDictionary(m => m.SecurityId);

        var entered = currentIds.Except(previousIds).Select(id => currentById[id]).ToList();
        var exited = previousIds.Except(currentIds).Select(id => previousById[id]).ToList();

        return new DiffResult(entered, exited);
    }
}
