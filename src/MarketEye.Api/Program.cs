using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog;
using MarketEye.Infrastructure.DependencyInjection;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Ingestion.Jobs;
using MarketEye.Infrastructure.Screening;
using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Application Insights is optional locally: with no connection string the SDK stays
// inert, so a developer needs no Azure resource to run the stack.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddMarketEyeInfrastructure(builder.Configuration);
builder.Services.AddScoped<DailyIngestionJob>();
builder.Services.AddOpenApi();

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
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MarketEyeDbContext>();
    await db.Database.MigrateAsync();
    await MetricConceptSeed.SeedAsync(db, CancellationToken.None);
}

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new
{
    name = "MarketEye",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    disclaimer = "Educational purposes only. Not investment advice.",
}));

// The controlled vocabulary (§5.2). Exposed because it is a feature, not an implementation
// detail: a user who disagrees with what "cheap" means must be able to see and edit the number.
app.MapGet("/api/concepts", async (MarketEyeDbContext db, CancellationToken ct) =>
    Results.Ok(await db.MetricConcepts.AsNoTracking()
        .OrderBy(c => c.Name)
        .Select(c => new
        {
            c.Name, c.DisplayName, c.Description, c.Unit,
            c.MinValue, c.MaxValue, c.DefaultThreshold, c.DefaultOperator,
        })
        .ToListAsync(ct)));

app.MapPost("/api/screen", async (
    ScreenRequest request,
    ScreeningEngine engine,
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

app.Run();

/// <summary>Request body for POST /api/screen.</summary>
public sealed record ScreenRequest(ScreenCriteria Criteria, DateOnly? AsOfDate);

/// <summary>Exposed so MarketEye.IntegrationTests can drive the host with WebApplicationFactory.</summary>
public partial class Program;
