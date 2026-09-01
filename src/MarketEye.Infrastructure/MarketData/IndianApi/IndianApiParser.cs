using System.Globalization;
using System.Text.Json;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.MarketData.IndianApi;

/// <summary>
/// Maps an indianapi.in <c>/stock</c> response onto domain entities (PLAN.md §4.1, §4.3, §4.4).
///
/// Everything here is defensive. The response is deeply nested, sparsely populated, and encodes
/// numbers as strings — a single missing branch must yield "no data" rather than an exception that
/// aborts a run partway through a quota-limited pass.
/// </summary>
public static class IndianApiParser
{
    /// <summary>
    /// Extracts corporate actions. Ratio factors come from <see cref="CorporateActionRatioParser"/>
    /// and are left null when the remark cannot be parsed confidently — a null factor is skipped by
    /// <c>PriceAdjuster</c>, which leaves a visible discontinuity rather than a wrong adjustment.
    /// </summary>
    public static List<CorporateAction> ParseCorporateActions(JsonDocument doc, int securityId)
    {
        var result = new List<CorporateAction>();
        if (!doc.RootElement.TryGetProperty("stockCorporateActionData", out var ca)
            || ca.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var (property, type) in new[]
                 {
                     ("bonus", CorporateActionType.Bonus),
                     ("splits", CorporateActionType.Split),
                     ("rights", CorporateActionType.Rights),
                     ("dividend", CorporateActionType.Dividend),
                 })
        {
            if (!ca.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in list.EnumerateArray())
            {
                var action = ParseAction(item, type, securityId);
                if (action is not null) result.Add(action);
            }
        }
        return result;
    }

    private static CorporateAction? ParseAction(JsonElement item, CorporateActionType type, int securityId)
    {
        // Ex-date is what the price series steps on. The provider names it differently per action
        // type (xbDate for bonus, xdDate for dividend), so several are tried before falling back
        // to the record date.
        var effective = FirstDate(item, "exDate", "xbDate", "xdDate", "sortDate", "recordDate");
        if (effective is null) return null;

        var remarks = Str(item, "remarks");

        var action = new CorporateAction
        {
            SecurityId = securityId,
            EffectiveDate = effective.Value,
            ActionType = type,
            RawDescription = remarks,
        };

        switch (type)
        {
            case CorporateActionType.Bonus:
                action.AdjustmentFactor = CorporateActionRatioParser.BonusFactor(remarks);
                break;

            case CorporateActionType.Split:
                action.AdjustmentFactor = CorporateActionRatioParser.SplitFactor(remarks);
                break;

            case CorporateActionType.Rights:
                // Deliberately left null. Dilution needs the cum-rights market price, which is not
                // in the response; the adjustment pass computes it from the price series.
                action.AdjustmentFactor = null;
                break;

            case CorporateActionType.Dividend:
                // The one action type with a structured amount — no prose parsing needed.
                action.DividendAmount = Dec(item, "value");
                break;
        }
        return action;
    }

    /// <summary>
    /// Extracts fundamentals per fiscal period.
    ///
    /// ReportedDate is ESTIMATED (see <see cref="ReportingLag"/>) because the provider supplies
    /// none. Each row is flagged so the approximation stays visible downstream.
    /// </summary>
    public static List<Fundamentals> ParseFundamentals(JsonDocument doc, int securityId)
    {
        var result = new List<Fundamentals>();
        if (!doc.RootElement.TryGetProperty("financials", out var financials)
            || financials.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var statement in financials.EnumerateArray())
        {
            var endDate = FirstDate(statement, "EndDate");
            if (endDate is null) continue;

            var isAnnual = ReportingLag.IsAnnual(Str(statement, "Type"));

            if (!statement.TryGetProperty("stockFinancialMap", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var income = Section(map, "INC");
            var balance = Section(map, "BAL");

            var f = new Fundamentals
            {
                SecurityId = securityId,
                FiscalPeriodEnd = endDate.Value,
                ReportedDate = ReportingLag.EstimateReportedDate(endDate.Value, isAnnual),
                IsReportedDateEstimated = true,
                Revenue = Metric(income, "TotalRevenue", "Revenue"),
                NetIncome = Metric(income, "NetIncome", "NetIncomeAfterTaxes"),
                TotalDebt = Metric(balance, "TotalDebt", "LongTermDebt"),
                ShareholdersEquity = Metric(balance, "TotalEquity", "TotalLiabilitiesShareholders'Equity"),
            };

            // A row with no usable figures is noise; storing it would put empty periods into the
            // temporal table and make point-in-time reads return blanks instead of the last real
            // filing.
            if (f.Revenue is null && f.NetIncome is null &&
                f.TotalDebt is null && f.ShareholdersEquity is null)
            {
                continue;
            }
            result.Add(f);
        }
        return result;
    }

    private static JsonElement? Section(JsonElement map, string name) =>
        map.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Array ? s : null;

    /// <summary>
    /// Values live as {key, value} pairs in an array, so a lookup is a scan. Several candidate keys
    /// are accepted because the provider's naming varies across statement layouts.
    /// </summary>
    private static decimal? Metric(JsonElement? section, params string[] keys)
    {
        if (section is null) return null;

        foreach (var key in keys)
        {
            foreach (var entry in section.Value.EnumerateArray())
            {
                if (!entry.TryGetProperty("key", out var k)) continue;
                if (!string.Equals(k.GetString(), key, StringComparison.OrdinalIgnoreCase)) continue;

                if (entry.TryGetProperty("value", out var v)
                    && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                {
                    return d;
                }
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static DateOnly? FirstDate(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            var raw = Str(e, name);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        }
        return null;
    }
}
