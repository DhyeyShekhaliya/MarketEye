using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using MarketEye.Infrastructure.Screening;
using Testcontainers.MsSql;
using Xunit;
using MarketEye.IntegrationTests;

namespace MarketEye.IntegrationTests.Persistence;

/// <summary>
/// End-to-end through real SQL Server: migrations, bulk ingest, snapshot seal, compile, execute.
///
/// The two tests that matter are the point-in-time ones. Everything else here is plumbing; those
/// two are the properties that make every downstream result trustworthy or worthless.
/// </summary>
public class ScreeningPipelineTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;

        // Production registers these inside AddMarketEyeInfrastructure, but this test builds the
        // ScreeningEngine by hand to keep the DI container out of a persistence test. That means
        // the Dapper handlers have to be registered explicitly: ScreenRow.PriceDate is a DateOnly,
        // and without the handler Dapper cannot materialise the row at all. Register() is
        // idempotent (see DateOnlyTypeHandlerTests).
        DapperTypeHandlers.Register();

        await _sql.StartAsync(TestContext.Current.CancellationToken);
        _cs = _sql.GetConnectionString();

        _db = new MarketEyeDbContext(
            new DbContextOptionsBuilder<MarketEyeDbContext>().UseSqlServer(_cs).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await MetricConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);
        await StrategyConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    private async Task<int> AddSecurityAsync(string ticker, bool active, string? delisted = null)
    {
        var s = new Security
        {
            Ticker = ticker, ProviderSecurityId = $"INE{ticker}01018",
            Name = ticker + " Ltd", Exchange = "NSE", Sector = "Technology",
            IsActive = active,
            DelistedDate = delisted is null ? null : DateOnly.Parse(delisted),
            DelistingReason = delisted is null ? null : DelistingReason.Bankruptcy,
        };
        _db.Securities.Add(s);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return s.Id;
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Bulk_ingest_is_idempotent()
    {
        // §10 Phase 1 requires idempotent re-runs. A plain bulk insert would throw on the second
        // pass; the MERGE makes a retry after a partial failure safe.
        var ct = TestContext.Current.CancellationToken;
        var id = await AddSecurityAsync("ALPHA", active: true);

        var bars = Enumerable.Range(0, 10).Select(i => new PriceBar
        {
            SecurityId = id, Date = new DateOnly(2024, 1, 1).AddDays(i),
            Open = 100, High = 105, Low = 95, Close = 100 + i, AdjClose = 100 + i, Volume = 1000,
        }).ToList();

        var writer = new PriceBarBulkWriter(_cs);
        await writer.WriteAsync(bars, ct);
        await writer.WriteAsync(bars, ct);

        await using var conn = new SqlConnection(_cs);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PriceBars WHERE SecurityId = @id", new { id });

        count.Should().Be(10, "re-ingesting the same day must update in place, not duplicate");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_empty_snapshot_cannot_be_sealed()
    {
        // A silent download failure and a market holiday both produce zero rows. Sealing the first
        // would publish a day on which every security appears to have vanished.
        var ct = TestContext.Current.CancellationToken;
        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(new DateOnly(2024, 1, 10), "test/1", ct);

        var act = async () => await lifecycle.SealAsync(snap.Id, priceRows: 0, fundamentalRows: 0, ct);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*zero price rows*");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_unsealed_snapshot_is_invisible_to_readers()
    {
        // §4.5's atomic-failure property: a half-finished job leaves something nothing reads.
        var ct = TestContext.Current.CancellationToken;
        var lifecycle = new SnapshotLifecycle(_db);
        await lifecycle.OpenAsync(new DateOnly(2024, 2, 1), "test/1", ct);

        var latest = await lifecycle.LatestSealedAsync(new DateOnly(2024, 12, 31), ct);
        latest.Should().BeNull();
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_sealed_snapshot_cannot_be_sealed_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(new DateOnly(2024, 3, 1), "test/1", ct);
        await lifecycle.SealAsync(snap.Id, 100, 10, ct);

        var act = async () => await lifecycle.SealAsync(snap.Id, 200, 20, ct);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already sealed*");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_screen_includes_a_security_that_delisted_after_the_as_of_date()
    {
        // THE survivorship test, end to end. GAMMA went bankrupt in June. A screen run as of
        // January must still return it, because it was trading in January. A pipeline that drops
        // it produces backtests that are wrong in a consistently flattering direction.
        var ct = TestContext.Current.CancellationToken;
        var alpha = await AddSecurityAsync("ALPHA", active: true);
        var gamma = await AddSecurityAsync("GAMMA", active: false, delisted: "2024-06-28");

        var bars = new List<PriceBar>();
        foreach (var id in new[] { alpha, gamma })
        {
            bars.Add(new PriceBar
            {
                SecurityId = id, Date = new DateOnly(2024, 1, 31),
                Open = 100, High = 100, Low = 100, Close = 100, AdjClose = 100, Volume = 1000,
            });
        }
        await new PriceBarBulkWriter(_cs).WriteAsync(bars, ct);

        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(new DateOnly(2024, 1, 31), "test/1", ct);
        await lifecycle.SealAsync(snap.Id, bars.Count, 0, ct);

        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, ct);
        var engine = new ScreeningEngine(
            _db, new CriteriaCompiler(vocab), new ScreenCriteriaValidator(vocab), _cs);

        var criteria = new ScreenCriteria
        {
            Universe = new UniverseConstraint { Exchange = "NSE" },
            Root = new Group
            {
                Op = GroupOperator.And,
                Children = [new Comparison
                {
                    Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 1m,
                }],
            },
        };

        var result = await engine.RunAsync(criteria, snap, null, ct);

        result.Rows.Select(r => r.Ticker).Should().Contain("GAMMA",
            "GAMMA was trading on 2024-01-31; excluding it is survivorship bias (§7, §8.2)");
        result.Rows.Should().HaveCount(2);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_screen_does_not_see_fundamentals_reported_after_the_as_of_date()
    {
        // §4.1's reporting-lag half, through the real query. The filing exists in the table and
        // covers a period that already ended -- it is only the ReportedDate that hides it.
        var ct = TestContext.Current.CancellationToken;
        var id = await AddSecurityAsync("BETA", active: true);

        await new PriceBarBulkWriter(_cs).WriteAsync([new PriceBar
        {
            SecurityId = id, Date = new DateOnly(2024, 4, 15),
            Open = 100, High = 100, Low = 100, Close = 100, AdjClose = 100, Volume = 1000,
        }], ct);

        await using (var conn = new SqlConnection(_cs))
        {
            // Fiscal period ended 31 March; the market only learns the numbers on 2 May.
            await conn.ExecuteAsync("""
                INSERT INTO dbo.FundamentalRatios (SecurityId, ReportedDate, Pe, Basis)
                VALUES (@id, '2024-05-02', 8.0, 'Consolidated');
                """, new { id });
        }

        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(new DateOnly(2024, 4, 15), "test/1", ct);
        await lifecycle.SealAsync(snap.Id, 1, 1, ct);

        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, ct);
        var engine = new ScreeningEngine(
            _db, new CriteriaCompiler(vocab), new ScreenCriteriaValidator(vocab), _cs);

        var criteria = new ScreenCriteria
        {
            Universe = UniverseConstraint.All,
            Root = new Group
            {
                Op = GroupOperator.And,
                Children = [new Comparison
                {
                    Field = "PeRatio", Operator = ComparisonOperator.LessThan, Value = 10m,
                }],
            },
        };

        var result = await engine.RunAsync(criteria, snap, null, ct);

        result.Rows.Should().BeEmpty(
            "a P/E of 8 was not knowable on 2024-04-15; the filing was published on 2024-05-02");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_screen_run_is_recorded_against_its_snapshot()
    {
        // §4.5: reproducibility depends on the run remembering which snapshot it resolved against.
        var ct = TestContext.Current.CancellationToken;
        var id = await AddSecurityAsync("DELTA", active: true);
        await new PriceBarBulkWriter(_cs).WriteAsync([new PriceBar
        {
            SecurityId = id, Date = new DateOnly(2024, 5, 1),
            Open = 50, High = 50, Low = 50, Close = 50, AdjClose = 50, Volume = 10,
        }], ct);

        var lifecycle = new SnapshotLifecycle(_db);
        var snap = await lifecycle.OpenAsync(new DateOnly(2024, 5, 1), "test/1", ct);
        await lifecycle.SealAsync(snap.Id, 1, 0, ct);

        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, ct);
        var engine = new ScreeningEngine(
            _db, new CriteriaCompiler(vocab), new ScreenCriteriaValidator(vocab), _cs);

        var criteria = new ScreenCriteria
        {
            Universe = UniverseConstraint.All,
            Root = new Group
            {
                Op = GroupOperator.And,
                Children = [new Comparison
                {
                    Field = "ClosePrice", Operator = ComparisonOperator.GreaterThan, Value = 1m,
                }],
            },
        };
        await engine.RunAsync(criteria, snap, null, ct);

        var run = await _db.ScreenRuns.AsNoTracking().SingleAsync(ct);
        run.SnapshotId.Should().Be(snap.Id);
        run.ResultCount.Should().Be(1);

        // And the stored criteria round-trip, or "re-run it later" is an empty promise.
        var restored = ScreenCriteriaJson.Deserialize(run.CriteriaJson);
        restored.Root.Comparisons().Single().Field.Should().Be("ClosePrice");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task The_seeded_vocabulary_loads_and_rejects_unknown_concepts()
    {
        var ct = TestContext.Current.CancellationToken;
        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, ct);

        vocab.All.Should().NotBeEmpty();
        vocab.Find("PeRatio").Should().NotBeNull();
        vocab.Find("CheapnessScore").Should().BeNull("§5.1: unknown concepts fail closed");

        // The DB-backed vocabulary must match the validator ordinally, or validation and
        // compilation would disagree about what exists.
        vocab.Find("peratio").Should().BeNull();
    }
}
