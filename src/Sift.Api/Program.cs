using Microsoft.EntityFrameworkCore;
using Serilog;
using Sift.Infrastructure.DependencyInjection;
using Sift.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Application Insights is optional locally: with no connection string the SDK stays
// inert, so a developer needs no Azure resource to run the stack.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddSiftInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("Sift")!,
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
    await scope.ServiceProvider.GetRequiredService<SiftDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new
{
    name = "Sift",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    disclaimer = "Educational purposes only. Not investment advice.",
}));

app.Run();

/// <summary>Exposed so Sift.IntegrationTests can drive the host with WebApplicationFactory.</summary>
public partial class Program;
