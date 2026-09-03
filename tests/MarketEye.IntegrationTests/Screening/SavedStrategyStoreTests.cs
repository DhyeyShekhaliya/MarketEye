using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Screening;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.IntegrationTests.Screening;

/// <summary>
/// Saved strategies are "core workflow, not polish" (PLAN.md §10). The write path validates
/// before storing -- mirroring StrategyConceptStore -- so a saved strategy can never reach the
/// point of failing the first time someone clicks "Run" instead of when they saved it.
/// </summary>
public class SavedStrategyStoreTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private SavedStrategyStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;

        await _sql.StartAsync(TestContext.Current.CancellationToken);
        _db = new MarketEyeDbContext(
            new DbContextOptionsBuilder<MarketEyeDbContext>().UseSqlServer(_sql.GetConnectionString()).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await MetricConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);

        var vocab = await DbMetricConceptVocabulary.LoadAsync(_db, TestContext.Current.CancellationToken);
        _store = new SavedStrategyStore(_db, new ScreenCriteriaValidator(vocab));
    }

    public async ValueTask DisposeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    private static ScreenCriteria ValidCriteria(decimal peBelow = 25m) => new()
    {
        Universe = UniverseConstraint.All,
        Root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison
            {
                Field = "PeRatio", Operator = ComparisonOperator.LessThan, Value = peBelow,
            }],
        },
    };

    private static SavedStrategyDraft Draft(
        string name = "my_value_screen", ScreenCriteria? criteria = null) => new()
    {
        Name = name,
        Description = "A test strategy",
        OriginalPrompt = "cheap stocks",
        Criteria = criteria ?? ValidCriteria(),
    };

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_valid_strategy_is_created_and_readable()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _store.CreateAsync(Draft(), ct);

        result.Succeeded.Should().BeTrue();
        var stored = await _db.SavedStrategies.SingleAsync(s => s.Name == "my_value_screen", ct);
        stored.OriginalPrompt.Should().Be("cheap stocks");

        // The prompt is provenance only -- what re-runs is CriteriaJson, and it must round-trip
        // through the exact same serialiser the rest of the system uses (§4.5's reproducibility
        // promise applies here too).
        var roundTripped = ScreenCriteriaJson.Deserialize(stored.CriteriaJson);
        roundTripped.Should().BeEquivalentTo(ValidCriteria());
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Criteria_naming_an_unknown_metric_is_rejected_before_storage()
    {
        // The exact property StrategyConceptValidator guarantees for concept definitions, proven
        // here for saved strategies: a bad definition never reaches the table.
        var ct = TestContext.Current.CancellationToken;
        var bad = new ScreenCriteria
        {
            Universe = UniverseConstraint.All,
            Root = new Group
            {
                Op = GroupOperator.And,
                Children = [new Comparison
                {
                    Field = "NoSuchMetric", Operator = ComparisonOperator.LessThan, Value = 1m,
                }],
            },
        };

        var result = await _store.CreateAsync(Draft(criteria: bad), ct);

        result.Succeeded.Should().BeFalse();
        result.Validation.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.UnknownConcept);
        (await _db.SavedStrategies.AnyAsync(ct)).Should().BeFalse(
            "nothing must be stored when validation fails");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_duplicate_name_is_rejected_as_a_conflict_not_a_validation_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft(), ct);

        var result = await _store.CreateAsync(Draft(), ct);

        result.Succeeded.Should().BeFalse();
        result.Conflict.Should().Be("name-in-use");
        (await _db.SavedStrategies.CountAsync(s => s.Name == "my_value_screen", ct)).Should().Be(1);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Updating_replaces_the_criteria_and_bumps_UpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft(), ct);
        var before = await _db.SavedStrategies.AsNoTracking()
            .SingleAsync(s => s.Name == "my_value_screen", ct);

        var result = await _store.UpdateAsync(
            "my_value_screen", Draft(criteria: ValidCriteria(peBelow: 10m)), ct);

        result.Succeeded.Should().BeTrue();
        var after = await _db.SavedStrategies.AsNoTracking()
            .SingleAsync(s => s.Name == "my_value_screen", ct);
        ScreenCriteriaJson.Deserialize(after.CriteriaJson).Root.Comparisons().Single().Value
            .Should().Be(10m);
        after.UpdatedAt.Should().BeAfter(before.UpdatedAt);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Renaming_to_a_name_another_strategy_already_owns_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft("first"), ct);
        await _store.CreateAsync(Draft("second"), ct);

        var result = await _store.UpdateAsync("second", Draft("first"), ct);

        result.Succeeded.Should().BeFalse();
        result.Conflict.Should().Be("name-in-use");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Sharing_issues_a_token_and_re_sharing_is_idempotent()
    {
        // PLAN.md §10 Phase 4 "Strategy sharing": read-only links, not full auth.
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft(), ct);

        var first = await _store.EnableSharingAsync("my_value_screen", ct);
        var second = await _store.EnableSharingAsync("my_value_screen", ct);

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first, "re-sharing must not rotate the token and break a link already handed out");

        var stored = await _db.SavedStrategies.AsNoTracking().SingleAsync(s => s.Name == "my_value_screen", ct);
        stored.ShareToken.Should().Be(first);
        stored.SharedAt.Should().NotBeNull();
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Unsharing_clears_the_token_so_the_old_link_stops_resolving()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft(), ct);
        var token = await _store.EnableSharingAsync("my_value_screen", ct);

        (await _store.DisableSharingAsync("my_value_screen", ct)).Should().BeTrue();

        var stored = await _db.SavedStrategies.AsNoTracking().SingleAsync(s => s.Name == "my_value_screen", ct);
        stored.ShareToken.Should().BeNull();
        stored.SharedAt.Should().BeNull();
        (await _db.SavedStrategies.AnyAsync(s => s.ShareToken == token, ct)).Should().BeFalse(
            "the old token must not resolve to anything once sharing is disabled");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Sharing_an_unknown_strategy_returns_null_rather_than_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _store.EnableSharingAsync("does_not_exist", ct)).Should().BeNull();
        (await _store.DisableSharingAsync("does_not_exist", ct)).Should().BeFalse();
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Deleting_removes_it_and_reports_a_missing_name_honestly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateAsync(Draft(), ct);

        (await _store.DeleteAsync("my_value_screen", ct)).Should().BeTrue();
        (await _db.SavedStrategies.AnyAsync(ct)).Should().BeFalse();
        (await _store.DeleteAsync("my_value_screen", ct)).Should().BeFalse(
            "deleting something already gone is a no-op, not an error");
    }
}
