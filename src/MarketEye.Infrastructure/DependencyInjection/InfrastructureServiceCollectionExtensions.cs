using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketEye.Application.MarketData;
using MarketEye.Infrastructure.MarketData;
using System.Net;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using MarketEye.Infrastructure.MarketData.IndianApi;
using MarketEye.Infrastructure.Persistence;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using MarketEye.Infrastructure.Screening;
using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddMarketEyeInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        // Dapper cannot map SQL date -> DateOnly on its own, and the failure is a runtime cast
        // exception on the first real query. Registered once, at composition.
        DapperTypeHandlers.Register();

        // Deliberately NOT throwing here.
        //
        // Throwing during service registration kills the process before any logging is wired up.
        // On App Service that surfaces as a bare "ContainerStartupFailure" with no application
        // output at all -- the operator is told the container died but not why, and has to go
        // hunting through platform logs. That cost real time on the first deployment.
        //
        // Instead the app boots, /health reports Unhealthy, and the reason is a readable string
        // over HTTP. A misconfigured app that can explain itself beats one that vanishes.
        var cs = config.GetConnectionString("MarketEye");

        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.Error.WriteLine(
                "STARTUP WARNING: ConnectionStrings:MarketEye is not configured. The app will " +
                "start but /health will report Unhealthy. Locally: copy .env.example to .env and " +
                "run 'docker compose up -d'. On Azure: App Service > Environment variables > " +
                "Connection strings, name 'MarketEye', type SQLAzure.");

            // A syntactically valid string pointing nowhere. EF can build its model, the health
            // check runs and fails with a real message, and nothing silently reads a live database.
            cs = "Server=tcp:unconfigured,1433;Database=unconfigured;Connection Timeout=1;";
        }

        services.AddDbContext<MarketEyeDbContext>(o => o.UseSqlServer(cs, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sql.CommandTimeout(60);
        }));

        // Phase 0 ships the interface and a fixture stub only. PLAN.md §12 makes the
        // backfill/pricing analysis the FIRST Phase 1 task, so choosing EODHD vs FMP
        // now would commit to a pricing tier before that analysis exists.
        services.AddSingleton<IMarketDataProvider, FixtureMarketDataProvider>();

        services.AddSingleton(new PriceBarBulkWriter(cs));
        services.AddSingleton<BhavcopyParser>();
        services.AddSingleton(new IndicatorBulkWriter(cs));
        services.AddSingleton<IsinResolver>();
        services.AddScoped<BackfillService>();
        services.AddScoped<MarketData.RequestBudget>();
        services.AddScoped(sp => new DelistingDetector(
            cs, sp.GetRequiredService<ILoggerFactory>().CreateLogger<DelistingDetector>()));

        // NSE rejects plain HTTP clients. A cookie container is mandatory (the archive endpoints
        // require session cookies from the homepage) and so is a browser-like User-Agent.
        services.AddHttpClient<NseBhavcopyClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        .AddStandardResilienceHandler();

        // Backfill reads a cloned mirror; the nightly job goes to NSE. Set Ingestion:ArchivePath
        // to switch. Scraping 1,250 files from NSE is what gets an IP blocked.
        services.AddScoped<IBhavcopySource>(sp =>
        {
            var archivePath = config["Ingestion:ArchivePath"];
            return string.IsNullOrWhiteSpace(archivePath)
                ? sp.GetRequiredService<NseBhavcopyClient>()
                : new LocalArchiveBhavcopySource(archivePath);
        });

        services.AddScoped<BhavcopyIngestionService>();

        services.AddHttpClient<IndianApiClient>(client =>
        {
            client.BaseAddress = new Uri(
                config["Provider:IndianApi:BaseUrl"] ?? "https://stock.indianapi.in");
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .AddStandardResilienceHandler();

        services.AddScoped<FundamentalsIngestionService>();
        services.AddScoped<SnapshotLifecycle>();

        // The vocabulary is ~20 rows read on nearly every request, so it is loaded once per scope
        // rather than per comparison -- otherwise the validator's inner loop hits the database.
        services.AddScoped<IMetricConceptVocabulary>(sp =>
            DbMetricConceptVocabulary
                .LoadAsync(sp.GetRequiredService<MarketEyeDbContext>(), CancellationToken.None)
                .GetAwaiter().GetResult());

        services.AddScoped<ScreenCriteriaValidator>();
        services.AddScoped<CriteriaCompiler>();
        services.AddScoped(sp => new ScreeningEngine(
            sp.GetRequiredService<MarketEyeDbContext>(),
            sp.GetRequiredService<CriteriaCompiler>(),
            sp.GetRequiredService<ScreenCriteriaValidator>(),
            cs));

        return services;
    }
}
