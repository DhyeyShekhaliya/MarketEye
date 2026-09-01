using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Infers delistings from absence in the bhavcopy record (PLAN.md §7, §8.2).
///
/// The archive is survivorship-free by construction — a company that stopped trading in 2023 is
/// still present in every file up to its last session. But nothing in the file *says* it delisted;
/// it simply stops appearing. Without this pass every security looks active forever, and:
///
/// - §7 cannot price a delisting exit, because there is no DelistedDate to exit on;
/// - §8.2's survivorship guard has nothing to assert, since it compares against IsActive;
/// - a backtest silently holds positions in companies that ceased to exist.
///
/// The data was never biased. The *interpretation* was missing, which is the more dangerous of the
/// two because the row counts all look right.
/// </summary>
public sealed class DelistingDetector(string connectionString, ILogger<DelistingDetector> logger)
{
    /// <summary>
    /// A security absent for this many trading sessions before the dataset's end is treated as
    /// delisted. Generous on purpose: Indian securities can be suspended for weeks and resume, and
    /// wrongly marking a live company delisted removes it from screens. The opposite error only
    /// delays recognition.
    /// </summary>
    public const int AbsenceThresholdDays = 60;

    public async Task<DelistingReport> DetectAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var datasetEnd = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(Date) FROM dbo.PriceBars");

        if (datasetEnd is null)
        {
            logger.LogWarning("No price bars; nothing to detect.");
            return new DelistingReport(0, 0, null);
        }

        var cutoff = datasetEnd.Value.AddDays(-AbsenceThresholdDays);

        // DelistingReason stays Unknown rather than guessed. §7 prices a bankruptcy exit at zero
        // and every other exit at the last traded price, so inventing a reason here would put a
        // fabricated number into backtest results. Absence tells us the security stopped trading;
        // it does not tell us why.
        var marked = await conn.ExecuteAsync(new CommandDefinition("""
            SET QUOTED_IDENTIFIER ON;

            WITH LastBar AS (
                SELECT SecurityId, MAX(Date) AS LastDate
                FROM dbo.PriceBars
                GROUP BY SecurityId
            )
            UPDATE s
            SET s.IsActive = 0,
                s.DelistedDate = lb.LastDate,
                s.DelistingReason = 'Unknown'
            FROM dbo.Securities s
            JOIN LastBar lb ON lb.SecurityId = s.Id
            WHERE lb.LastDate < @cutoff AND s.IsActive = 1;
            """, new { cutoff }, cancellationToken: ct));

        var stillActive = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Securities WHERE IsActive = 1");

        logger.LogInformation(
            "Delisting detection: {Marked} marked inactive (last bar before {Cutoff:yyyy-MM-dd}), " +
            "{Active} still active", marked, cutoff, stillActive);

        return new DelistingReport(marked, stillActive, DateOnly.FromDateTime(cutoff));
    }
}

public sealed record DelistingReport(int MarkedDelisted, int StillActive, DateOnly? Cutoff);
