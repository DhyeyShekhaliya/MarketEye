using Dapper;
using Microsoft.Data.SqlClient;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Reconciliation;

/// <summary>
/// Checks stored adjustment factors against what the market actually did (PLAN.md §12).
///
/// §12 requires splits and dividends verified against a hand-checked sample of 20 securities
/// before Phase 1 is done. Hand-checking 20 securities against a second source is slow and gets
/// skipped; this does the arithmetic and leaves a human to judge the outliers.
///
/// The idea: on an ex-date the raw price steps by the action's economics. A 1:1 bonus should
/// roughly halve it. So the price step IMPLIES a factor, which can be compared against the factor
/// parsed from the provider's prose:
///
///     impliedFactor = close(ex-date) / close(previous session)
///
/// Agreement means the parsed ratio and the market's repricing tell the same story. Disagreement
/// means one of them is wrong, and it is nearly always the prose parsing (ADR-0004: a bonus quoted
/// "1:1" and a split quoted "2-for-1" are the same economics with inverted numbers).
///
/// This is a signal, not proof. Ordinary market movement on the ex-date is mixed into the step, so
/// small deviations are expected and a tolerance is applied.
/// </summary>
public sealed class CorporateActionReconciler(string connectionString)
{
    /// <summary>
    /// How far the implied factor may sit from the stored one before it is flagged. Ex-dates carry
    /// normal price movement on top of the adjustment, so a few percent is noise rather than error.
    /// </summary>
    public const decimal ToleranceFraction = 0.05m;

    /// <summary>
    /// How far a comparison bar may sit from the ex-date.
    ///
    /// Without this the "previous session" lookup happily returns a bar from a year earlier when
    /// the action predates the ingested window — and then reports a confident implied factor that
    /// is really twelve months of ordinary price movement. The first run produced exactly that: a
    /// dividend with an implied factor of 2.27, and three different RELIANCE actions all showing
    /// the same 2583.30 -> 1353.90 pair.
    ///
    /// A reconciliation that manufactures plausible-looking noise is worse than one that reports
    /// nothing, because the noise gets investigated.
    /// </summary>
    public const int MaxBarDistanceDays = 10;

    public async Task<ReconciliationReport> RunAsync(int maxSecurities, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Only price-affecting actions with a factor can be checked this way. A dividend moves the
        // price by its yield, which is usually inside the noise band, so those are reported
        // separately rather than pass/failed on a step comparison.
        var rows = (await conn.QueryAsync<ActionRow>(new CommandDefinition("""
            SELECT TOP (@take)
                s.Id AS SecurityId, s.Ticker, ca.EffectiveDate, ca.ActionType,
                ca.AdjustmentFactor, ca.DividendAmount, ca.RawDescription,
                (SELECT TOP 1 pb.[Close] FROM dbo.PriceBars pb
                  WHERE pb.SecurityId = s.Id AND pb.Date >= ca.EffectiveDate
                    AND pb.Date <= DATEADD(day, @window, ca.EffectiveDate)
                  ORDER BY pb.Date ASC) AS CloseOnOrAfter,
                (SELECT TOP 1 pb.[Close] FROM dbo.PriceBars pb
                  WHERE pb.SecurityId = s.Id AND pb.Date < ca.EffectiveDate
                    AND pb.Date >= DATEADD(day, -@window, ca.EffectiveDate)
                  ORDER BY pb.Date DESC) AS CloseBefore,
                (SELECT TOP 1 pb.AdjClose FROM dbo.PriceBars pb
                  WHERE pb.SecurityId = s.Id AND pb.Date >= ca.EffectiveDate
                    AND pb.Date <= DATEADD(day, @window, ca.EffectiveDate)
                  ORDER BY pb.Date ASC) AS AdjOnOrAfter,
                (SELECT TOP 1 pb.AdjClose FROM dbo.PriceBars pb
                  WHERE pb.SecurityId = s.Id AND pb.Date < ca.EffectiveDate
                    AND pb.Date >= DATEADD(day, -@window, ca.EffectiveDate)
                  ORDER BY pb.Date DESC) AS AdjBefore,
                -- Actions sharing an ex-date produce ONE price step between them. Comparing any
                -- single factor against that step is wrong by construction: 360ONE had a 1:1 bonus
                -- AND a Rs.2 -> Re.1 split on 2023-03-02, so the step reflects both.
                (SELECT COUNT(*) FROM dbo.CorporateActions x
                  WHERE x.SecurityId = ca.SecurityId AND x.EffectiveDate = ca.EffectiveDate
                    AND x.ActionType IN ('Split','Bonus','Rights')) AS PriceActionsOnDate,
                (SELECT EXP(SUM(LOG(x.AdjustmentFactor))) FROM dbo.CorporateActions x
                  WHERE x.SecurityId = ca.SecurityId AND x.EffectiveDate = ca.EffectiveDate
                    AND x.ActionType IN ('Split','Bonus','Rights')
                    AND x.AdjustmentFactor > 0) AS CombinedFactorOnDate
            FROM dbo.CorporateActions ca
            JOIN dbo.Securities s ON s.Id = ca.SecurityId
            WHERE ca.ActionType IN ('Split', 'Bonus', 'Rights', 'Dividend')
            ORDER BY ca.EffectiveDate DESC;
            """, new { take = maxSecurities * 5, window = MaxBarDistanceDays }, cancellationToken: ct))).ToList();

        var report = new ReconciliationReport();

        foreach (var r in rows)
        {
            if (r.CloseBefore is not > 0 || r.CloseOnOrAfter is not > 0)
            {
                // No price on one side of the ex-date, so nothing to compare. Common for actions
                // that predate the ingested window.
                report.Skipped++;
                continue;
            }

            var implied = r.CloseOnOrAfter.Value / r.CloseBefore.Value;

            var check = new ActionCheck
            {
                Ticker = r.Ticker,
                EffectiveDate = r.EffectiveDate,
                ActionType = r.ActionType,
                StoredFactor = r.AdjustmentFactor,
                ImpliedFactor = Math.Round(implied, 4),
                RawDescription = r.RawDescription,
                RawCloseBefore = r.CloseBefore,
                RawCloseAfter = r.CloseOnOrAfter,
            };

            // Compare against the COMBINED factor for the date when several actions share it.
            var comparisonFactor = r.PriceActionsOnDate > 1
                ? r.CombinedFactorOnDate
                : r.AdjustmentFactor;

            check.ActionsSharingDate = r.PriceActionsOnDate;
            check.ComparisonFactor = comparisonFactor is null ? null : Math.Round(comparisonFactor.Value, 4);

            if (comparisonFactor is { } stored && stored > 0)
            {
                var deviation = Math.Abs(implied - stored) / stored;
                check.DeviationFraction = Math.Round(deviation, 4);
                check.Status = deviation <= ToleranceFraction
                    ? ReconciliationStatus.Agrees
                    : ReconciliationStatus.Disagrees;
            }
            else if (r.ActionType == nameof(CorporateActionType.Dividend))
            {
                check.Status = ReconciliationStatus.NotApplicable;
            }
            else
            {
                // A price-affecting action with no factor: the prose could not be parsed, so no
                // adjustment was applied and the series still has a step in it.
                check.Status = ReconciliationStatus.Unadjusted;
            }

            // Independent of the factor: after adjustment the series should be continuous across
            // the ex-date. A surviving step means the adjustment did not reach these bars.
            if (r.AdjBefore is > 0 && r.AdjOnOrAfter is > 0)
            {
                var adjStep = Math.Abs(r.AdjOnOrAfter.Value - r.AdjBefore.Value) / r.AdjBefore.Value;
                check.AdjustedSeriesStep = Math.Round(adjStep, 4);
            }

            report.Checks.Add(check);
        }

        report.Agreed = report.Checks.Count(c => c.Status == ReconciliationStatus.Agrees);
        report.Disagreed = report.Checks.Count(c => c.Status == ReconciliationStatus.Disagrees);
        report.Unadjusted = report.Checks.Count(c => c.Status == ReconciliationStatus.Unadjusted);
        report.DistinctSecurities = report.Checks.Select(c => c.Ticker).Distinct().Count();

        return report;
    }

    private sealed record ActionRow(
        int SecurityId, string Ticker, DateOnly EffectiveDate, string ActionType,
        decimal? AdjustmentFactor, decimal? DividendAmount, string? RawDescription,
        decimal? CloseOnOrAfter, decimal? CloseBefore, decimal? AdjOnOrAfter, decimal? AdjBefore,
        int PriceActionsOnDate, decimal? CombinedFactorOnDate);
}

public enum ReconciliationStatus
{
    /// <summary>Stored factor matches the price step within tolerance.</summary>
    Agrees,
    /// <summary>Stored factor and price step disagree — inspect this one by hand.</summary>
    Disagrees,
    /// <summary>Price-affecting action with no parsed factor; the series still has a step.</summary>
    Unadjusted,
    /// <summary>Dividend: the price move is usually inside the noise band, so no verdict.</summary>
    NotApplicable,
}

public sealed class ActionCheck
{
    public required string Ticker { get; init; }
    public DateOnly EffectiveDate { get; init; }
    public required string ActionType { get; init; }
    public decimal? StoredFactor { get; init; }

    /// <summary>Number of price-affecting actions sharing this ex-date. More than one means the
    /// price step reflects all of them combined.</summary>
    public int ActionsSharingDate { get; set; }

    /// <summary>The factor actually compared: the stored one, or the product when several share a date.</summary>
    public decimal? ComparisonFactor { get; set; }
    public decimal? ImpliedFactor { get; init; }
    public decimal? DeviationFraction { get; set; }
    public decimal? AdjustedSeriesStep { get; set; }
    public decimal? RawCloseBefore { get; init; }
    public decimal? RawCloseAfter { get; init; }
    public string? RawDescription { get; init; }
    public ReconciliationStatus Status { get; set; }
}

public sealed class ReconciliationReport
{
    public List<ActionCheck> Checks { get; } = [];
    public int DistinctSecurities { get; set; }
    public int Agreed { get; set; }
    public int Disagreed { get; set; }
    public int Unadjusted { get; set; }
    public int Skipped { get; set; }

    /// <summary>§12 asks for 20 securities. Below that the sample is too small to conclude from.</summary>
    public bool MeetsSampleRequirement => DistinctSecurities >= 20;
}
