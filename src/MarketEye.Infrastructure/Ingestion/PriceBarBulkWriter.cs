using System.Data;
using Microsoft.Data.SqlClient;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Ingestion;

/// <summary>
/// Bulk-inserts price bars (PLAN.md §3).
///
/// EF Core is never used on this path. Its change tracker allocates and tracks per entity, which
/// is the right trade at hundreds of rows and the wrong one at millions — ingestion would spend
/// its time in the tracker rather than in the network round trip.
///
/// Writes to a temp table then MERGEs, which makes re-running a day idempotent. §10 Phase 1
/// requires idempotent re-runs, and a plain bulk insert into a table with a primary key would
/// throw on the second run instead.
/// </summary>
public sealed class PriceBarBulkWriter(string connectionString)
{
    public async Task<int> WriteAsync(IReadOnlyList<PriceBar> bars, CancellationToken ct)
    {
        if (bars.Count == 0) return 0;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE #PriceBarStage (
                    SecurityId INT NOT NULL, Date DATE NOT NULL,
                    [Open] DECIMAL(18,4) NOT NULL, High DECIMAL(18,4) NOT NULL,
                    Low DECIMAL(18,4) NOT NULL, [Close] DECIMAL(18,4) NOT NULL,
                    AdjClose DECIMAL(18,4) NOT NULL, Volume BIGINT NOT NULL,
                    IsCircuitLocked BIT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
               {
                   DestinationTableName = "#PriceBarStage",
                   BatchSize = 10_000,
                   BulkCopyTimeout = 300,
               })
        {
            foreach (var c in new[]
                     {
                         "SecurityId", "Date", "Open", "High", "Low",
                         "Close", "AdjClose", "Volume", "IsCircuitLocked",
                     })
            {
                bulk.ColumnMappings.Add(c, c);
            }
            await bulk.WriteToServerAsync(ToTable(bars), ct);
        }

        await using (var merge = conn.CreateCommand())
        {
            merge.Transaction = tx;
            merge.CommandTimeout = 300;
            // MERGE rather than INSERT: re-ingesting a day updates in place instead of throwing on
            // the primary key, which is what makes a failed run safe to simply retry.
            merge.CommandText = """
                MERGE dbo.PriceBars AS target
                USING #PriceBarStage AS source
                    ON target.SecurityId = source.SecurityId AND target.Date = source.Date
                WHEN MATCHED THEN UPDATE SET
                    target.[Open] = source.[Open], target.High = source.High,
                    target.Low = source.Low, target.[Close] = source.[Close],
                    target.AdjClose = source.AdjClose, target.Volume = source.Volume,
                    target.IsCircuitLocked = source.IsCircuitLocked
                WHEN NOT MATCHED THEN INSERT
                    (SecurityId, Date, [Open], High, Low, [Close], AdjClose, Volume, IsCircuitLocked)
                    VALUES (source.SecurityId, source.Date, source.[Open], source.High,
                            source.Low, source.[Close], source.AdjClose, source.Volume,
                            source.IsCircuitLocked);
                """;
            await merge.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return bars.Count;
    }

    private static DataTable ToTable(IReadOnlyList<PriceBar> bars)
    {
        var t = new DataTable();
        t.Columns.Add("SecurityId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Open", typeof(decimal));
        t.Columns.Add("High", typeof(decimal));
        t.Columns.Add("Low", typeof(decimal));
        t.Columns.Add("Close", typeof(decimal));
        t.Columns.Add("AdjClose", typeof(decimal));
        t.Columns.Add("Volume", typeof(long));
        t.Columns.Add("IsCircuitLocked", typeof(bool));

        foreach (var b in bars)
        {
            t.Rows.Add(b.SecurityId, b.Date.ToDateTime(TimeOnly.MinValue),
                b.Open, b.High, b.Low, b.Close, b.AdjClose, b.Volume, b.IsCircuitLocked);
        }
        return t;
    }
}
