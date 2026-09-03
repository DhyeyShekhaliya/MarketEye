using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace MarketEye.Infrastructure.MarketData.Benchmark;

/// <summary>
/// Loads a total-return index history from a locally-provided CSV, under a caller-supplied ticker
/// (`docs/adr/0010`; generalised beyond NIFTY 50 alone in PLAN.md §10 Phase 4 "Additional
/// benchmarks").
///
/// No ingestion path in this codebase touches index-level data — bhavcopy is equity-only — and
/// niftyindices.com's historical-data export is a manual download, not a bulk API. Mirrors
/// `LocalArchiveBhavcopySource`'s "read a local file" shape rather than `NseBhavcopyClient`'s
/// "scrape live" shape: this is quarterly-refresh reference data, not a nightly job, so it is run
/// as a one-off admin command, never wired into `DailyIngestionJob`.
///
/// Expected CSV format (the shape niftyindices.com's own historical-data export uses, for ANY of
/// its indices -- NIFTY 50 TR, NIFTY 500 TR, or a sector index -- not just NIFTY 50):
/// <code>
/// Date,Close
/// 01-Sep-2021,20821.87
/// 02-Sep-2021,20913.53
/// </code>
/// The "Close" column here is the TOTAL RETURN index value, not the price index — §7 requires the
/// total-return series specifically, since comparing AdjClose-based backtest returns against the
/// price index would understate the benchmark the same way §4.4 warns about for individual
/// securities. Download the "<index> TR" series, not the plain price index, from niftyindices.com's
/// historical data page, and pass the ticker you want it stored under (e.g. "NIFTY50TR",
/// "NIFTY500TR") to <see cref="LoadAsync"/> -- it is never inferred from the file.
/// </summary>
public sealed class NiftyTotalReturnLoader(string connectionString)
{
    /// <summary>
    /// Parses the CSV and upserts every row into <c>BenchmarkPrices</c> under <paramref name="ticker"/>.
    /// Idempotent — re-running against an updated export just overwrites matching (Ticker, Date)
    /// rows, the same MERGE pattern the ingest path already uses for idempotent re-runs (§10 Phase 1).
    /// </summary>
    public async Task<int> LoadAsync(string ticker, string csvPath, CancellationToken ct)
    {
        if (!File.Exists(csvPath))
        {
            throw new FileNotFoundException(
                $"Benchmark CSV not found at '{csvPath}'. Download the '{ticker}' total-return " +
                "historical data export from niftyindices.com and pass its path here.", csvPath);
        }

        var rows = ParseCsv(csvPath).ToList();
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{csvPath}' parsed to zero rows. Check the file is the expected Date,Close CSV " +
                "and not empty or a different format.");
        }

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        foreach (var row in rows)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                MERGE dbo.BenchmarkPrices AS target
                USING (SELECT @ticker AS Ticker, @date AS [Date]) AS source
                ON target.Ticker = source.Ticker AND target.[Date] = source.[Date]
                WHEN MATCHED THEN
                    UPDATE SET TotalReturnIndexValue = @value
                WHEN NOT MATCHED THEN
                    INSERT (Ticker, [Date], TotalReturnIndexValue)
                    VALUES (@ticker, @date, @value);
                """,
                new { ticker, date = row.Date, value = row.Value },
                transaction: (SqlTransaction)transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return rows.Count;
    }

    private static IEnumerable<(DateOnly Date, decimal Value)> ParseCsv(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null) yield break;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            if (!DateOnly.TryParseExact(
                    parts[0].Trim(), "dd-MMM-yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                && !DateOnly.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out date))
            {
                continue; // Skip unparseable rows rather than fail the whole load on one bad line.
            }

            if (!decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            yield return (date, value);
        }
    }
}
