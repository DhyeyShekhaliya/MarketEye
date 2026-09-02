using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Ai;
using MarketEye.Infrastructure.MarketData;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Screening;
using Testcontainers.MsSql;
using Xunit;

namespace MarketEye.IntegrationTests.Ai;

/// <summary>
/// Proves the two properties that only exist once RequestBudget and HybridCache are real (PLAN.md
/// §5.4, §5.5): the daily budget survives being backed by an actual database, and editing the
/// vocabulary invalidates the parse cache by construction rather than by an expiry guess.
///
/// A unit test cannot cover this: RequestBudget is a concrete class over MarketEyeDbContext, not
/// an interface, by design (§3 keeps EF for CRUD, and adding a seam here only to satisfy a mock
/// would be exactly the kind of interface PLAN.md's §14 rejects — "IBenchmarkProvider... one
/// implementation exists").
/// </summary>
public class IntentTranslationServiceTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private MarketEyeDbContext _db = null!;
    private HybridCache _cache = null!;
    private RequestBudget _budget = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerGate.Enabled) return;

        await _sql.StartAsync(TestContext.Current.CancellationToken);
        _db = new MarketEyeDbContext(
            new DbContextOptionsBuilder<MarketEyeDbContext>().UseSqlServer(_sql.GetConnectionString()).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await MetricConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);
        await StrategyConceptSeed.SeedAsync(_db, TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddHybridCache();
        _cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        _budget = new RequestBudget(_db, NullLogger<RequestBudget>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (!DockerGate.Enabled) return;
        await _db.DisposeAsync();
        await _sql.DisposeAsync();
    }

    /// <summary>Counts calls and returns a fixed intent, so cache behaviour is directly observable.</summary>
    private sealed class CountingParser(ParsedIntent intent, bool consumesBudget = true) : IIntentParser
    {
        public int Calls;
        public string Describe => "counting-fake";
        public bool ConsumesBudget => consumesBudget;

        public Task<ParseOutcome> ParseAsync(string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<ParseOutcome>(new ParseOutcome.Parsed(intent));
        }
    }

    private static readonly ParsedIntent Cheap = new()
    {
        Concepts = ["cheap"], ExplicitFilters = [],
    };

    /// <summary>
    /// Each [Fact] gets its own class instance and its own container (xUnit's default), so every
    /// test's ApiCallBudget row starts empty -- no per-test provider key is needed to keep the
    /// daily counters from colliding.
    /// </summary>
    private async Task<IntentTranslationService> BuildAsync(IIntentParser parser, int dailyCap)
    {
        var vocabulary = await DbStrategyConceptVocabulary.LoadAsync(
            _db, TestContext.Current.CancellationToken);
        var metrics = await DbMetricConceptVocabulary.LoadAsync(
            _db, TestContext.Current.CancellationToken);
        var resolver = new IntentResolver(vocabulary, metrics, new ScreenCriteriaValidator(metrics));

        return new IntentTranslationService(
            parser, resolver, vocabulary, _cache, _budget, dailyCap,
            NullLogger<IntentTranslationService>.Instance);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Repeating_the_same_prompt_hits_the_cache_and_calls_the_model_once()
    {
        var parser = new CountingParser(Cheap);
        var service = await BuildAsync(parser, dailyCap: 10);

        await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        await service.TranslateAsync("  Cheap   stocks  ", TestContext.Current.CancellationToken);
        await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);

        parser.Calls.Should().Be(1,
            "the second and third calls normalise to the same key and must hit the cache -- " +
            "repeat phrasings must cost zero tokens (§5.5)");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_distinct_prompt_still_calls_the_model()
    {
        var parser = new CountingParser(Cheap);
        var service = await BuildAsync(parser, dailyCap: 10);

        await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        await service.TranslateAsync("profitable stocks", TestContext.Current.CancellationToken);

        parser.Calls.Should().Be(2, "two different prompts must not collide on one cache key");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_exhausted_daily_budget_degrades_to_a_clarification_not_a_crash()
    {
        var parser = new CountingParser(Cheap);
        var service = await BuildAsync(parser, dailyCap: 1);

        var first = await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        var second = await service.TranslateAsync("profitable stocks", TestContext.Current.CancellationToken);

        first.IsResolved.Should().BeTrue();

        // §5.6: unavailable degrades to a question, never to an exception or a guessed screen.
        second.NeedsClarification.Should().BeTrue();
        second.IsResolved.Should().BeFalse();
        parser.Calls.Should().Be(1, "the budget must be checked BEFORE the call, not after");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task An_exhausted_budget_does_not_poison_the_cache_for_tomorrow()
    {
        // HybridCache must never store a value when the factory throws. If it did, the SAME
        // prompt made after the budget resets would return yesterday's failure forever.
        var parser = new CountingParser(Cheap);
        var service = await BuildAsync(parser, dailyCap: 1);

        await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        var denied = await service.TranslateAsync(
            "profitable stocks", TestContext.Current.CancellationToken);
        denied.NeedsClarification.Should().BeTrue();

        // Simulate the budget resetting (a new day) by building a fresh service with headroom,
        // but reusing the SAME cache instance -- the denied prompt must not be cached as a value.
        var recovered = await BuildAsync(parser, dailyCap: 10);
        var retried = await recovered.TranslateAsync(
            "profitable stocks", TestContext.Current.CancellationToken);

        retried.IsResolved.Should().BeTrue("a fresh budget must be able to answer a previously-denied prompt");
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task A_parser_that_consumes_no_budget_is_never_rate_limited_by_the_budget()
    {
        // The keyword stub's whole purpose is to keep working when the paid budget is spent.
        // Charging it against the same counter would let it disable itself by its own usage.
        var parser = new CountingParser(Cheap, consumesBudget: false);
        var service = await BuildAsync(parser, dailyCap: 1);

        await service.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        var second = await service.TranslateAsync(
            "profitable stocks", TestContext.Current.CancellationToken);

        second.IsResolved.Should().BeTrue();
        parser.Calls.Should().Be(2);
    }

    [Fact(Skip = DockerGate.SkipReason, SkipUnless = nameof(DockerGate.Enabled), SkipType = typeof(DockerGate))]
    public async Task Editing_the_vocabulary_invalidates_previously_cached_parses()
    {
        var parser = new CountingParser(Cheap);

        var before = await BuildAsync(parser, dailyCap: 10);
        await before.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);
        parser.Calls.Should().Be(1);

        // VersionToken hashes each row's NAME, ENABLED STATE and DEFINITION -- deliberately not
        // UpdatedAt, so a restored backup or a direct SQL edit still invalidates correctly (see
        // DbStrategyConceptVocabulary.ComputeVersion). So the edit that must change the token is a
        // real content change, the same field StrategyConceptStore.UpdateAsync writes on a save.
        var cheap = await _db.StrategyConcepts.FirstAsync(
            c => c.Name == "cheap", TestContext.Current.CancellationToken);
        cheap.DefinitionJson = """{"kind":"group","op":"And","children":[{"kind":"comparison","field":"PeRatio","operator":"LessThan","value":18}]}""";
        cheap.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var after = await BuildAsync(parser, dailyCap: 10);
        await after.TranslateAsync("cheap stocks", TestContext.Current.CancellationToken);

        parser.Calls.Should().Be(2,
            "the same prompt after a vocabulary edit must miss the old cache entry, not replay it");
    }
}

