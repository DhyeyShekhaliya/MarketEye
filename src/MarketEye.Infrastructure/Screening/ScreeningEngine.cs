using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Executes a validated screen against a sealed snapshot (PLAN.md §4.5, §6).
///
/// Reads go through Dapper rather than EF: this is a projection onto a flat result shape with no
/// change tracking to gain from, and §3 reserves EF for CRUD and configuration.
/// </summary>
public sealed class ScreeningEngine(
    MarketEyeDbContext db,
    CriteriaCompiler compiler,
    ScreenCriteriaValidator validator,
    string connectionString)
{
    public async Task<ScreenResult> RunAsync(
        ScreenCriteria criteria, DataSnapshot snapshot, int? savedStrategyId, CancellationToken ct)
    {
        // §8.2 guards run before anything touches the database.
        PointInTimeGuard.RequireSealed(snapshot);

        var validation = validator.Validate(criteria);
        if (!validation.IsValid)
        {
            // The engine re-validates rather than assuming the caller did. §5.1's boundary is only
            // a boundary if everything crossing it is checked.
            throw new InvalidOperationException(
                "Criteria failed validation: " +
                string.Join("; ", validation.Errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        var compiled = compiler.Compile(criteria, snapshot);

        var parameters = new DynamicParameters();
        foreach (var (k, v) in compiled.Parameters) parameters.Add(k, v);

        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(connectionString);
        var rows = (await conn.QueryAsync<ScreenRow>(
            new CommandDefinition(compiled.Sql, parameters, cancellationToken: ct))).ToList();
        sw.Stop();

        db.ScreenRuns.Add(new ScreenRun
        {
            SnapshotId = snapshot.Id,
            CriteriaJson = ScreenCriteriaJson.Serialize(criteria),
            RunAt = DateTimeOffset.UtcNow,
            ResultCount = rows.Count,
            DurationMs = (int)sw.ElapsedMilliseconds,
            SavedStrategyId = savedStrategyId,
            // Populated only when a saved strategy is behind this run (Phase 4 "Alerts" diffs
            // consecutive runs' member sets) -- an ad hoc /api/screen call has nothing to diff
            // against, so it does not pay this storage cost.
            MemberSecuritiesJson = savedStrategyId is null
                ? null
                : JsonSerializer.Serialize(rows.Select(r => new { r.Id, r.Ticker, r.Name })),
        });
        await db.SaveChangesAsync(ct);

        return new ScreenResult
        {
            Rows = rows,
            SnapshotId = snapshot.Id,
            AsOfDate = snapshot.AsOfDate,
            DurationMs = (int)sw.ElapsedMilliseconds,
        };
    }
}

public sealed record ScreenRow(
    int Id, string Ticker, string Name, string Exchange,
    string? Sector, string? Industry,
    decimal Close, decimal AdjClose, DateOnly PriceDate);

public sealed record ScreenResult
{
    public required IReadOnlyList<ScreenRow> Rows { get; init; }
    public required int SnapshotId { get; init; }
    public required DateOnly AsOfDate { get; init; }
    public required int DurationMs { get; init; }

    /// <summary>
    /// Always false from ScreeningEngine itself; CachedScreeningEngine sets this true on a hit
    /// (§5.5), so an API caller can tell a fast response apart from a cached one instead of
    /// seeing the original run's stale DurationMs repeated on every subsequent hit.
    /// </summary>
    public bool FromCache { get; init; }
}
