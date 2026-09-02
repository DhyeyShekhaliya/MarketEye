using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MarketEye.Ai;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using MarketEye.UnitTests.Screening;
using Xunit;

namespace MarketEye.UnitTests.Ai;

/// <summary>
/// The response-handling half of the NIM client, tested offline against canned HTTP responses.
///
/// The schema constrains the model, but nothing here trusts that constraint held: a model that
/// silently ignored it, an HTTP error, or a malformed filter must all degrade to
/// <see cref="ParseOutcome.Unavailable"/> — never throw, and never hand the resolver something
/// that looks like a valid intent but is not (§5.6).
/// </summary>
public class NvidiaIntentParserTests
{
    /// <summary>Returns a fixed response regardless of the outgoing request.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static NvidiaIntentParser Parser(
        HttpStatusCode status, string body, string apiKey = "test-key") =>
        new(
            new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://example.invalid/") },
            Options.Create(new AiOptions { ApiKey = apiKey, Model = "test-model" }),
            new TestStrategyVocabulary(),
            SeededMetricVocabulary.Instance,
            NullLogger<NvidiaIntentParser>.Instance);

    private static string ChatResponse(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" +
        System.Text.Json.JsonSerializer.Serialize(content) +
        "}}]}";

    [Fact]
    public async Task No_api_key_is_unavailable_without_making_a_network_call()
    {
        // A backstop behind the DI-level choice of StubIntentParser (AiServiceCollectionExtensions):
        // this class must refuse to call out even if it is ever constructed without a key.
        var parser = Parser(HttpStatusCode.OK, "irrelevant", apiKey: "");

        var outcome = await parser.ParseAsync("cheap stocks", TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<ParseOutcome.Unavailable>();
    }

    [Fact]
    public async Task A_well_formed_reply_becomes_a_ParsedIntent()
    {
        var body = ChatResponse("""
            {"concepts":["cheap","small_cap"],"explicit_filters":[],"clarification":null}
            """);
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("cheap small caps", TestContext.Current.CancellationToken);

        var intent = outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
        intent.Concepts.Should().BeEquivalentTo(["cheap", "small_cap"]);
        intent.ExplicitFilters.Should().BeEmpty();
        intent.Clarification.Should().BeNull();
    }

    [Fact]
    public async Task An_explicit_filter_is_read_correctly()
    {
        var body = ChatResponse("""
            {"concepts":["profitable"],
             "explicit_filters":[{"field":"PeRatio","operator":"LessThan","value":12}],
             "clarification":null}
            """);
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync(
            "profitable with P/E below 12", TestContext.Current.CancellationToken);

        var intent = outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
        intent.ExplicitFilters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Field = "PeRatio", Operator = ComparisonOperator.LessThan, Value = 12m,
            });
    }

    [Fact]
    public async Task A_clarification_is_read_as_a_clarifying_intent()
    {
        var body = ChatResponse("""
            {"concepts":[],"explicit_filters":[],"clarification":"What do you mean by good?"}
            """);
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("good stocks", TestContext.Current.CancellationToken);

        var intent = outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
        intent.Clarification.Should().Be("What do you mean by good?");
    }

    [Fact]
    public async Task An_http_error_status_is_unavailable_not_an_exception()
    {
        var parser = Parser(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}""");

        var outcome = await parser.ParseAsync("cheap stocks", TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<ParseOutcome.Unavailable>();
    }

    [Fact]
    public async Task A_model_that_ignores_the_schema_is_unavailable_not_a_crash()
    {
        // The schema is a property of the provider's decoder, not a guarantee the client can
        // trust. A model that returned prose instead of JSON must not throw an unhandled
        // exception into a user's request.
        var body = ChatResponse("Sure! Here are some cheap stocks for you...");
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("cheap stocks", TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<ParseOutcome.Unavailable>();
    }

    [Fact]
    public async Task An_empty_response_body_is_unavailable()
    {
        var body = """{"choices":[{"message":{"content":null}}]}""";
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("cheap stocks", TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<ParseOutcome.Unavailable>();
    }

    [Fact]
    public async Task A_malformed_filter_is_dropped_rather_than_guessed_at()
    {
        // Missing the operator. Inventing a default here would be exactly the "model picked the
        // number" failure §5.1 exists to prevent -- the concept still comes through.
        var body = ChatResponse("""
            {"concepts":["cheap"],
             "explicit_filters":[{"field":"PeRatio","value":12}],
             "clarification":null}
            """);
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("cheap stocks", TestContext.Current.CancellationToken);

        var intent = outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
        intent.Concepts.Should().Contain("cheap");
        intent.ExplicitFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unresolvable_concept_from_the_model_still_reaches_the_resolver_unfiltered()
    {
        // The parser's job is to read what the model said, not to police it -- IntentResolver is
        // the one place §5.1 is enforced (Step 2), and it must see the raw name to reject it.
        var body = ChatResponse("""
            {"concepts":["not_a_real_concept"],"explicit_filters":[],"clarification":null}
            """);
        var parser = Parser(HttpStatusCode.OK, body);

        var outcome = await parser.ParseAsync("something odd", TestContext.Current.CancellationToken);

        var intent = outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
        intent.Concepts.Should().Contain("not_a_real_concept");
    }
}
