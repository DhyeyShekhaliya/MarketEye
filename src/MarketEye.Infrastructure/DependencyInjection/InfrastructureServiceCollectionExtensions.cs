using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketEye.Application.MarketData;
using MarketEye.Infrastructure.MarketData;
using System.Net;
using MarketEye.Infrastructure.Ingestion;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using MarketEye.Infrastructure.Persistence;
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
        var cs = config.GetConnectionString("MarketEye")
                 ?? throw new InvalidOperationException(
                     "ConnectionStrings:MarketEye is not configured. Copy .env.example to .env and run 'docker compose up -d'.");

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
