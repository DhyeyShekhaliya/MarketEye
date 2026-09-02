using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Ai;

/// <summary>
/// Composition for the intent parser (PLAN.md §5.4).
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddMarketEyeAi(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection(AiOptions.SectionName));

        var options = new AiOptions();
        config.GetSection(AiOptions.SectionName).Bind(options);

        if (!options.IsConfigured)
        {
            // No key: the app still runs, with a weaker parser that asks rather than guesses.
            // §2's claim that the model can be removed entirely and the system below still works
            // is only true if this path exists and is the default rather than an error.
            Console.Error.WriteLine(
                "STARTUP NOTE: no Ai:ApiKey configured, so natural-language parsing uses the " +
                "keyword fallback. The manual screener and vocabulary are unaffected. Set one " +
                "with: dotnet user-secrets set \"Ai:ApiKey\" \"<key>\" --project src/MarketEye.Api");

            services.AddScoped<IIntentParser>(sp =>
                new StubIntentParser(sp.GetRequiredService<IStrategyConceptVocabulary>()));

            return services;
        }

        // Same shape as the two existing outbound clients (see
        // InfrastructureServiceCollectionExtensions): a typed client with the standard resilience
        // handler, so retries and timeouts are policy rather than hand-rolled per call.
        services.AddHttpClient<IIntentParser, NvidiaIntentParser>(client =>
        {
            client.BaseAddress = new Uri(
                options.Endpoint.EndsWith('/') ? options.Endpoint : options.Endpoint + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }).AddStandardResilienceHandler();

        return services;
    }
}
