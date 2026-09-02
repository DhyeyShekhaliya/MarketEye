using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using MarketEye.Infrastructure.DependencyInjection;
using MarketEye.Ai;
using MarketEye.Infrastructure.Ai;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Ingestion.Jobs;
using MarketEye.Infrastructure.Reconciliation;
using MarketEye.Infrastructure.Screening;
using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Vocabulary;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Application Insights is optional locally: with no connection string the SDK stays
// inert, so a developer needs no Azure resource to run the stack.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddMarketEyeInfrastructure(builder.Configuration);
builder.Services.AddMarketEyeAi(builder.Configuration);
builder.Services.AddScoped<DailyIngestionJob>();
// Enums cross the wire as names, matching ScreenCriteriaJson (Application/Screening).
//
// Without this, POST /api/screen rejects every request the Blazor UI sends: ScreenCriteria is full
// of enums -- GroupOperator, ComparisonOperator, SortDirection -- and the default web options bind
// them only from integers. The stored form of a ScreenRun already uses names, so leaving the HTTP
// surface on integers would also mean a criteria tree could not be posted back in the shape it was
// persisted in. The integration tests never caught this because they construct ScreenCriteria in
// C# and call the engine directly, never crossing the HTTP boundary.
builder.Services.ConfigureHttpJsonOptions(options =>
    ScreenCriteriaJson.ApplyWireFormat(options.SerializerOptions));

builder.Services.AddOpenApi();

// §5.4: 10/min and 100/day per caller on /api/parse. Built as two independently-partitioned
// limiters and combined with CreateChained rather than through RateLimiterOptions.AddPolicy --
// that API only accepts a single window per policy, and chaining is exactly what CreateChained is
// for: BOTH must allow the request, so tripping either window rejects it. Applied via an endpoint
// filter (below, on the /api/parse MapPost) rather than global middleware, so the limiter is
// enforced before IntentTranslationService's cache lookup and touches no other endpoint.
var aiParseLimiter = PartitionedRateLimiter.CreateChained(
    PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(AiParsePartitionKey(http), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 10,
            QueueLimit = 0,
        })),
    PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(AiParsePartitionKey(http), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromDays(1),
            PermitLimit = 100,
            QueueLimit = 0,
        })));

builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("MarketEye")!,
        name: "sql",
        tags: ["ready"]);

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Dev convenience only. Production (Azure) must apply migrations out of band via
    // 'dotnet ef database update' or a migration bundle: migrating on startup races
    // across scaled-out instances and gives no rollback path.
    // Migrations stay Development-only. Production applies them out of band, because startup
    // migration races across scaled-out instances and offers no rollback (docs/adr/0006).
    //
    // Wrapped so an unreachable database cannot kill the process. Crashing here produces a dead
    // app with no HTTP surface, which is strictly worse than a running app whose /health explains
    // the problem -- and it is what made the first Azure deployment so hard to diagnose.
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketEyeDbContext>().Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"STARTUP WARNING: migrations did not run: {ex.Message}\n" +
            "The app will start; /health will report the database as unhealthy. " +
            "If you are pointing at a database that is already migrated, this is safe to ignore.");
    }
}

// The vocabulary seeds in EVERY environment, unlike migrations.
//
// §5.2 makes MetricConcepts reference data, not test fixtures, and §5.1 fails closed on unknown
// concepts -- so an empty table does not degrade the app, it disables it: the validator rejects
// every concept and no screen can run. Seeding only in Development shipped an app to Azure that
// could serve /health but could not answer a single query.
//
// Safe to run on each start. MetricConceptSeed upserts by name -- those rows are system-owned so
// there is no user edit to clobber, and an existing database picks up new metrics. StrategyConcepts
// only inserts what is missing, because §5.2 makes those definitions user-editable and overwriting
// them would silently revert someone's considered change to what "cheap" means.
{
    using var seedScope = app.Services.CreateScope();
    var seedDb = seedScope.ServiceProvider.GetRequiredService<MarketEyeDbContext>();
    try
    {
        await MetricConceptSeed.SeedAsync(seedDb, CancellationToken.None);
        await StrategyConceptSeed.SeedAsync(seedDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        // Never let seeding stop the app from starting. An unreachable database is already
        // reported by /health; crashing here would hide that behind a container failure.
        Console.Error.WriteLine($"STARTUP WARNING: could not seed the vocabulary: {ex.Message}");
    }
}

// A health endpoint that only says "Unhealthy" forces whoever is on call to go log-diving.
// Reporting the failing check and its reason turns a deployment mystery into a readable answer.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            // Reveals whether config resolution worked at all, without exposing the credential:
            // the placeholder below is what the app substitutes when the setting is missing.
            connectionStringConfigured =
                !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MarketEye")),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                error = Redact(e.Value.Exception?.Message),
            }),
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    },
});

// SQL exceptions can echo the connection string. Strip anything password-shaped before it
// reaches an unauthenticated endpoint.
static string? Redact(string? message) => message is null
    ? null
    : Regex.Replace(message, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");

// The first X-Forwarded-For hop, since App Service terminates TLS and forwards through a proxy --
// RemoteIpAddress alone would partition every caller behind it under the load balancer's own
// address. No auth exists yet (§10 "Still open"): this is the exact line to change to key on a
// user id instead, once it does.
static string AiParsePartitionKey(HttpContext http)
{
    var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
    var firstHop = forwarded
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();

    return firstHop ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

app.MapGet("/", () => Results.Ok(new
{
    name = "MarketEye",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    disclaimer = "Educational purposes only. Not investment advice.",
}));

// The metric whitelist (§5.2, §6) -- what the manual filter builder offers and what a strategy
// concept's definition may name. Read-only by design: these rows carry the column names the
// compiler turns into SQL, so they are system-owned. The editable half of the vocabulary is
// /api/vocabulary/strategy-concepts (docs/adr/0007).
app.MapGet("/api/concepts", async (MarketEyeDbContext db, CancellationToken ct) =>
    Results.Ok(await db.MetricConcepts.AsNoTracking()
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            c.Name, c.DisplayName, c.Description, c.Unit, c.MinValue, c.MaxValue,
        })
        .ToListAsync(ct)));

// --- Strategy Vocabulary (§5.2) ------------------------------------------------------------
//
// The editable half of the vocabulary, and the reason the tables are split (docs/adr/0007): a user
// changes what "cheap" MEANS here, and never touches a column name. Every write is validated
// against the metric whitelist before it is stored, so a definition that reached the table is one
// the compiler can already run.

app.MapGet("/api/vocabulary/strategy-concepts", (
    IStrategyConceptVocabulary vocabulary, CriteriaExplainer explainer) =>
    Results.Ok(vocabulary.All
        .OrderBy(c => c.Name, StringComparer.Ordinal)
        .Select(c => new
        {
            c.Name,
            c.DisplayName,
            c.Description,
            c.Aliases,
            c.IsEnabled,
            c.IsSystem,
            Definition = c.Definition,
            // Pre-rendered so the panel and the vocabulary screen show the identical sentence.
            Explanation = explainer.Explain(c.Definition),
        })));

app.MapPost("/api/vocabulary/strategy-concepts", async (
    StrategyConceptRequest request, StrategyConceptStore store, CancellationToken ct) =>
{
    var result = await store.CreateAsync(request.ToDraft(), ct);
    return result.Succeeded
        ? Results.Created(
            $"/api/vocabulary/strategy-concepts/{result.Concept!.Name}", new { result.Concept.Name })
        : ValidationProblem(result.Validation);
});

app.MapPut("/api/vocabulary/strategy-concepts/{name}", async (
    string name, StrategyConceptRequest request, StrategyConceptStore store, CancellationToken ct) =>
{
    var result = await store.UpdateAsync(name, request.ToDraft(), ct);
    if (result.Conflict == "not-found") return Results.NotFound(new { name });

    return result.Succeeded
        ? Results.Ok(new { result.Concept!.Name })
        : ValidationProblem(result.Validation);
});

app.MapDelete("/api/vocabulary/strategy-concepts/{name}", async (
    string name, StrategyConceptStore store, CancellationToken ct) =>
{
    var problem = await store.DeleteAsync(name, ct);
    return problem switch
    {
        null => Results.NoContent(),
        "not-found" => Results.NotFound(new { name }),
        // 409, not 403: nothing about the caller is wrong, the row's state forbids it. Disabling
        // is the reversible way to express the same intent.
        _ => Results.Conflict(new
        {
            name,
            error = "Seeded concepts cannot be deleted. Disable it instead.",
        }),
    };
});

// --- Natural-language intent parsing (§5.1, §5.4) -------------------------------------------
//
// Never runs a screen: §5.3 forbids running from an unconfirmed parse. This only returns the
// interpretation panel's contents; POST /api/screen below executes the confirmed criteria.
app.MapPost("/api/parse", async (
    ParseRequest request,
    IntentTranslationService translator,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["prompt"] = ["A prompt is required."],
        });
    }

    var resolution = await translator.TranslateAsync(request.Prompt, ct);

    if (resolution.NeedsClarification)
    {
        return Results.Ok(new
        {
            clarification = resolution.Clarification,
            disclaimer = "Educational purposes only. Not investment advice.",
        });
    }

    if (!resolution.IsResolved)
    {
        // §5.1: an unknown concept is an answer, not a server error -- the panel needs to say
        // which word it did not recognise, in the same shape every other rejection uses.
        return ValidationProblem(CriteriaValidationResult.Failed(resolution.Errors));
    }

    return Results.Ok(new
    {
        criteria = resolution.Criteria,
        concepts = resolution.Concepts,
        explicitFilters = resolution.ExplicitFilters,
        disclaimer = "Educational purposes only. Not investment advice.",
    });
})
.AddEndpointFilter(async (context, next) =>
{
    using var lease = aiParseLimiter.AttemptAcquire(context.HttpContext, 1);
    if (!lease.IsAcquired)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return Results.Json(
            new { error = "Too many parse requests. Slow down and try again shortly." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    return await next(context);
});

app.MapPost("/api/screen", async (
    ScreenRequest request,
    CachedScreeningEngine engine,
    SnapshotLifecycle snapshots,
    CancellationToken ct) =>
{
    var asOf = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

    var snapshot = await snapshots.LatestSealedAsync(asOf, ct);
    if (snapshot is null)
    {
        // No sealed snapshot means no data has been ingested yet. Returning an empty result set
        // would look like "nothing matched", which is a different and much more misleading answer.
        return Results.Problem(
            detail: $"No sealed data snapshot exists at or before {asOf:yyyy-MM-dd}. Run ingestion first.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var result = await engine.RunAsync(request.Criteria, snapshot, ct);
        return Results.Ok(new
        {
            result.Rows,
            result.SnapshotId,
            result.AsOfDate,
            result.DurationMs,
            result.FromCache,
            disclaimer = "Educational purposes only. Not investment advice.",
        });
    }
    catch (InvalidOperationException ex)
    {
        // §5.1: validation failures are answers, not server errors. The caller needs to know
        // which concept was rejected so §5.3's panel can ask about it.
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["criteria"] = [ex.Message],
        });
    }
});

// --- Saved strategies (§10: "core workflow, not polish") -------------------------------------
//
// Stores resolved criteria, not the prompt that produced them: a saved strategy reproduces
// exactly even if the model or the vocabulary later changes. The write path validates the
// criteria before storing, mirroring /api/vocabulary/strategy-concepts, so a saved strategy can
// never reach the point of failing the first time someone clicks "Run".

app.MapGet("/api/strategies", async (MarketEyeDbContext db, CancellationToken ct) =>
{
    var strategies = await db.SavedStrategies.AsNoTracking()
        .OrderByDescending(s => s.UpdatedAt)
        .ToListAsync(ct);

    return Results.Ok(strategies.Select(ToDto));
});

app.MapGet("/api/strategies/{name}", async (
    string name, MarketEyeDbContext db, CancellationToken ct) =>
{
    var strategy = await db.SavedStrategies.AsNoTracking()
        .FirstOrDefaultAsync(s => s.Name == name, ct);
    return strategy is null ? Results.NotFound(new { name }) : Results.Ok(ToDto(strategy));
});

app.MapPost("/api/strategies", async (
    SavedStrategyRequest request, SavedStrategyStore store, CancellationToken ct) =>
{
    var result = await store.CreateAsync(request.ToDraft(), ct);
    if (result.Conflict == "name-in-use") return NameConflict(request.Name);

    return result.Succeeded
        ? Results.Created($"/api/strategies/{result.Strategy!.Name}", new { result.Strategy.Name })
        : ValidationProblem(result.Validation);
});

app.MapPut("/api/strategies/{name}", async (
    string name, SavedStrategyRequest request, SavedStrategyStore store, CancellationToken ct) =>
{
    var result = await store.UpdateAsync(name, request.ToDraft(), ct);
    if (result.Conflict == "not-found") return Results.NotFound(new { name });
    if (result.Conflict == "name-in-use") return NameConflict(request.Name);

    return result.Succeeded
        ? Results.Ok(new { result.Strategy!.Name })
        : ValidationProblem(result.Validation);
});

app.MapDelete("/api/strategies/{name}", async (
    string name, SavedStrategyStore store, CancellationToken ct) =>
    await store.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound(new { name }));

// Replays the strategy's stored criteria exactly as saved -- never the original prompt, which
// would re-run the model and could resolve differently if the vocabulary has since changed.
app.MapPost("/api/strategies/{name}/run", async (
    string name,
    DateOnly? asOfDate,
    MarketEyeDbContext db,
    CachedScreeningEngine engine,
    SnapshotLifecycle snapshots,
    CancellationToken ct) =>
{
    var strategy = await db.SavedStrategies.FirstOrDefaultAsync(s => s.Name == name, ct);
    if (strategy is null) return Results.NotFound(new { name });

    var asOf = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var snapshot = await snapshots.LatestSealedAsync(asOf, ct);
    if (snapshot is null)
    {
        return Results.Problem(
            detail: $"No sealed data snapshot exists at or before {asOf:yyyy-MM-dd}. Run ingestion first.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var criteria = ScreenCriteriaJson.Deserialize(strategy.CriteriaJson);
        var result = await engine.RunAsync(criteria, snapshot, ct);

        strategy.LastRunAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            result.Rows,
            result.SnapshotId,
            result.AsOfDate,
            result.DurationMs,
            result.FromCache,
            disclaimer = "Educational purposes only. Not investment advice.",
        });
    }
    catch (InvalidOperationException ex)
    {
        // A vocabulary edit since this strategy was saved can make its stored criteria invalid
        // today (§5.1: that is an answer, not a server error).
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["criteria"] = [ex.Message],
        });
    }
});

// Ingestion trigger. App Service F1 has no Always On, so an in-process timer never fires; an
// external cron calls this instead (docs/adr/0006). Shared-secret protected -- this endpoint
// writes data, so it must not be open.
app.MapPost("/api/ingest/trigger", (HttpContext http, IConfiguration config) =>
{
    var expected = config["Ingestion:TriggerSecret"];
    if (string.IsNullOrWhiteSpace(expected))
    {
        return Results.Problem(
            detail: "Ingestion:TriggerSecret is not configured; refusing to expose an unprotected write endpoint.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var provided = http.Request.Headers["X-Ingest-Secret"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { status = "ok", note = "POST /api/ingest/run performs the ingestion." });
});

// The actual ingestion run, kept separate from the auth check above for readability.
app.MapPost("/api/ingest/run", async (
    HttpContext http,
    IConfiguration config,
    BhavcopyIngestionService reader,
    DailyIngestionJob job,
    DateOnly? date,
    CancellationToken ct) =>
{
    var expected = config["Ingestion:TriggerSecret"];
    if (string.IsNullOrWhiteSpace(expected))
    {
        return Results.Problem(
            detail: "Ingestion:TriggerSecret is not configured; refusing to expose an unprotected write endpoint.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var provided = http.Request.Headers["X-Ingest-Secret"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
    {
        return Results.Unauthorized();
    }

    // Defaults to the most recent weekday. The cron fires after the Indian close, so "today" is
    // the session that just ended.
    var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    // Everything is inside the try. The fetch was previously outside it, so any failure reaching
    // NSE surfaced as a bare 500 with no body -- the cron reported "HTTP 500" three times and the
    // reason lived only in server logs.
    try
    {
        var day = await reader.ReadDayAsync(target, ct);
        if (day is null)
        {
            // A holiday or weekend. Not an error, and explicitly NOT a sealed empty snapshot.
            return Results.Ok(new { status = "no-data", date = target, note = "Holiday, weekend, or not yet published." });
        }

        var result = await job.RunAsync(target, day.Bars, "nse-bhavcopy/1", ct);

        return result.Succeeded
            ? Results.Ok(new { status = "sealed", date = target, rows = result.RowsWritten, snapshotId = result.SnapshotId })
            : Results.Problem(detail: result.Error, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Ingestion failed",
            detail: $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException is { } inner ? $" -> {inner.GetType().Name}: {inner.Message}" : ""),
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["date"] = target.ToString("yyyy-MM-dd"),
                // Distinguishes the two sources: unset means it went to NSE directly, which is
                // where a datacentre IP is most likely to be refused.
                ["source"] = string.IsNullOrWhiteSpace(config["Ingestion:ArchivePath"])
                    ? "nse-direct" : "local-archive",
            });
    }
});

// Backfill. Separate from the nightly endpoint because it uses the two-pass strategy: bars
// bulk-loaded first, indicators derived once at the end. Running the nightly path 1,250 times is
// O(days squared) and does not finish.
app.MapPost("/api/ingest/backfill", async (
    HttpContext http,
    IConfiguration config,
    BackfillService backfill,
    DateOnly from,
    DateOnly to,
    CancellationToken ct) =>
{
    var expected = config["Ingestion:TriggerSecret"];
    if (string.IsNullOrWhiteSpace(expected)) return Results.Problem(
        detail: "Ingestion:TriggerSecret is not configured.", statusCode: 503);

    var provided = http.Request.Headers["X-Ingest-Secret"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
    {
        return Results.Unauthorized();
    }

    var report = await backfill.RunAsync(from, to, ct);
    return Results.Ok(report);
});

// Fundamentals and corporate actions. Separate from the price ingest because it consumes the
// provider's 500/day quota, while the bhavcopy path does not (ADR-0005).
app.MapPost("/api/ingest/fundamentals", async (
    HttpContext http,
    IConfiguration config,
    FundamentalsIngestionService service,
    int? max,
    string? symbols,
    CancellationToken ct) =>
{
    var expected = config["Ingestion:TriggerSecret"];
    if (string.IsNullOrWhiteSpace(expected)) return Results.Problem(
        detail: "Ingestion:TriggerSecret is not configured.", statusCode: 503);

    var provided = http.Request.Headers["X-Ingest-Secret"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
    {
        return Results.Unauthorized();
    }

    try
    {
        // Defaults well under the daily allowance so an accidental call cannot spend it all.
        var explicitSymbols = symbols?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var report = await service.RunAsync(max ?? 50, explicitSymbols, ct);
        return Results.Ok(report);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Fundamentals ingestion failed",
            detail: $"{ex.GetType().Name}: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

// §12: splits and dividends verified against a sample of securities. Read-only, so it is not
// behind the ingestion secret -- it exposes no more than the screening endpoints already do.
app.MapGet("/api/reconcile/corporate-actions", async (
    CorporateActionReconciler reconciler,
    int? securities,
    CancellationToken ct) =>
{
    ReconciliationReport report;
    try
    {
        report = await reconciler.RunAsync(securities ?? 20, ct);
    }
    catch (Exception ex)
    {
        // An empty 500 body sent the last debugging round in the wrong direction.
        return Results.Problem(
            title: "Reconciliation failed",
            detail: $"{ex.GetType().Name}: {ex.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(new
    {
        report.DistinctSecurities,
        report.Agreed,
        report.Disagreed,
        report.Unadjusted,
        report.Skipped,
        report.MeetsSampleRequirement,
        // Disagreements first: those are the rows a human needs to look at, and burying them
        // under dozens of passes is how a reconciliation becomes a rubber stamp.
        checks = report.Checks
            .OrderBy(c => c.Status == ReconciliationStatus.Disagrees ? 0
                        : c.Status == ReconciliationStatus.Unadjusted ? 1 : 2)
            .ThenByDescending(c => c.DeviationFraction)
            .Take(60),
    });
});

app.Run();

/// <summary>
/// Renders validation errors the way /api/screen already does, so every rejection in the system
/// arrives in one shape. §5.3 shows these to the user, so the path matters as much as the message:
/// "definition.root.children[0].field" is what tells the panel which row to highlight.
/// </summary>
static IResult ValidationProblem(CriteriaValidationResult validation) =>
    Results.ValidationProblem(validation.Errors
        .GroupBy(e => e.Path)
        .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));

/// <summary>The shape every /api/strategies read returns.</summary>
static object ToDto(SavedStrategy s) => new
{
    s.Name,
    s.Description,
    s.OriginalPrompt,
    Criteria = ScreenCriteriaJson.Deserialize(s.CriteriaJson),
    s.CreatedAt,
    s.UpdatedAt,
    s.LastRunAt,
};

/// <summary>409, not 400: the request itself is well-formed, another row already owns the name.</summary>
static IResult NameConflict(string name) => Results.Conflict(new
{
    name,
    error = "A saved strategy with this name already exists.",
});

/// <summary>Request body for POST /api/screen.</summary>
public sealed record ScreenRequest(ScreenCriteria Criteria, DateOnly? AsOfDate);

/// <summary>Request body for POST /api/parse.</summary>
public sealed record ParseRequest(string Prompt);

/// <summary>
/// A create-or-update of a strategy concept. Carries the definition as a FilterNode rather than a
/// JSON string so the API's own deserialiser rejects a malformed tree before any of our code sees
/// it, and so validation stays pure tree work.
/// </summary>
public sealed record StrategyConceptRequest(
    string Name,
    string DisplayName,
    string? Description,
    string[]? Aliases,
    FilterNode Definition,
    bool? IsEnabled)
{
    public StrategyConceptDraft ToDraft() => new()
    {
        Name = Name,
        DisplayName = DisplayName,
        Description = Description,
        Aliases = Aliases ?? [],
        Definition = Definition,
        IsEnabled = IsEnabled ?? true,
    };
}

/// <summary>
/// A create-or-update of a saved strategy. Carries Criteria as a ScreenCriteria rather than a
/// JSON string for the same reason StrategyConceptRequest carries a FilterNode: a malformed tree
/// is rejected by the API's own deserialiser before any of our code sees it.
/// </summary>
public sealed record SavedStrategyRequest(
    string Name,
    string? Description,
    string? OriginalPrompt,
    ScreenCriteria Criteria)
{
    public SavedStrategyDraft ToDraft() => new()
    {
        Name = Name,
        Description = Description,
        OriginalPrompt = OriginalPrompt,
        Criteria = Criteria,
    };
}

/// <summary>Exposed so MarketEye.IntegrationTests can drive the host with WebApplicationFactory.</summary>
public partial class Program;
