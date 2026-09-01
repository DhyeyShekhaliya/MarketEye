namespace MarketEye.Domain.Entities;

/// <summary>
/// Raw reported figures, stored in a SYSTEM_VERSIONED temporal table (PLAN.md §4.1).
///
/// Reading this correctly needs BOTH conditions:
///   FOR SYSTEM_TIME AS OF @date   — handles restatements
///   ReportedDate &lt;= @date         — handles reporting lag
/// Either one alone is lookahead bias.
/// </summary>
public class Fundamentals
{
    public int SecurityId { get; set; }
    public Security? Security { get; set; }

    /// <summary>End of the fiscal period these figures describe.</summary>
    public DateOnly FiscalPeriodEnd { get; set; }

    /// <summary>
    /// When the market actually learned this. Distinct from <see cref="FiscalPeriodEnd"/>
    /// by the reporting lag, and the reason the temporal clause alone is insufficient.
    /// </summary>
    public DateOnly ReportedDate { get; set; }

    public decimal? Revenue { get; set; }
    public decimal? NetIncome { get; set; }
    public decimal? TotalDebt { get; set; }
    public decimal? ShareholdersEquity { get; set; }
}
