using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketEye.Application.MarketData;
using MarketEye.Infrastructure.MarketData;
using MarketEye.Infrastructure.Persistence;

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

        return services;
    }
}
