using System.Data;
using Microsoft.Data.SqlClient;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Bulk-writes indicator rows (PLAN.md §3, §4.3).
///
/// Exists for the same reason <see cref="PriceBarBulkWriter"/> does: a backfill produces millions
/// of indicator rows and EF's change tracker cannot carry that. MERGE keeps a re-run idempotent.
/// </summary>
public sealed class IndicatorBulkWriter(string connectionString)
{
    public async Task<int> WriteAsync(IReadOnlyList<IndicatorSet> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE #IndicatorStage (
                    SecurityId INT NOT NULL, Date DATE NOT NULL,
                    Sma50 DECIMAL(18,6) NULL, Sma200 DECIMAL(18,6) NULL, Rsi14 DECIMAL(18,6) NULL,
                    Macd DECIMAL(18,6) NULL, MacdSignal DECIMAL(18,6) NULL,
                    Atr14 DECIMAL(18,6) NULL, Vol30 DECIMAL(18,6) NULL);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
               {
                   DestinationTableName = "#IndicatorStage", BatchSize = 20_000, BulkCopyTimeout = 600,
               })
        {
            foreach (var c in new[] { "SecurityId", "Date", "Sma50", "Sma200", "Rsi14",
                                      "Macd", "MacdSignal", "Atr14", "Vol30" })
            {
                bulk.ColumnMappings.Add(c, c);
            }
            await bulk.WriteToServerAsync(ToTable(rows), ct);
        }

        await using (var merge = conn.CreateCommand())
        {
            merge.Transaction = tx;
            merge.CommandTimeout = 900;
            merge.CommandText = """
                MERGE dbo.Indicators AS t USING #IndicatorStage AS s
                    ON t.SecurityId = s.SecurityId AND t.Date = s.Date
                WHEN MATCHED THEN UPDATE SET
                    t.Sma50 = s.Sma50, t.Sma200 = s.Sma200, t.Rsi14 = s.Rsi14,
                    t.Macd = s.Macd, t.MacdSignal = s.MacdSignal,
                    t.Atr14 = s.Atr14, t.Vol30 = s.Vol30
                WHEN NOT MATCHED THEN INSERT
                    (SecurityId, Date, Sma50, Sma200, Rsi14, Macd, MacdSignal, Atr14, Vol30)
                    VALUES (s.SecurityId, s.Date, s.Sma50, s.Sma200, s.Rsi14,
                            s.Macd, s.MacdSignal, s.Atr14, s.Vol30);
                """;
            await merge.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private static DataTable ToTable(IReadOnlyList<IndicatorSet> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SecurityId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        foreach (var c in new[] { "Sma50", "Sma200", "Rsi14", "Macd", "MacdSignal", "Atr14", "Vol30" })
        {
            t.Columns.Add(c, typeof(decimal)).AllowDBNull = true;
        }

        foreach (var r in rows)
        {
            t.Rows.Add(r.SecurityId, r.Date.ToDateTime(TimeOnly.MinValue),
                (object?)r.Sma50 ?? DBNull.Value, (object?)r.Sma200 ?? DBNull.Value,
                (object?)r.Rsi14 ?? DBNull.Value, (object?)r.Macd ?? DBNull.Value,
                (object?)r.MacdSignal ?? DBNull.Value, (object?)r.Atr14 ?? DBNull.Value,
                (object?)r.Vol30 ?? DBNull.Value);
        }
        return t;
    }
}
