using EtsyShopManager.Etsy.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy;

public static class EtsyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Etsy client stack. The host must also register <see cref="IEtsyCredentialsProvider"/>
    /// and <see cref="IEtsyTokenStore"/> (both singletons).
    /// </summary>
    public static IServiceCollection AddEtsy(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EtsyOptions>(configuration.GetSection(EtsyOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<EtsyRateLimiter>();
        services.AddSingleton<EtsyQuotaTracker>();
        services.AddTransient<EtsyApiKeyHandler>();
        services.AddTransient<EtsyRetryHandler>();
        services.AddTransient<EtsyRateLimitHandler>();

        // Handler order: api key (outer) → retry → rate limit (inner, so every attempt takes a token).
        services.AddHttpClient(EtsyOAuthClient.HttpClientName, (sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<EtsyOptions>>().Value;
                client.BaseAddress = new Uri(o.ApiBaseUrl);
                client.Timeout = o.HttpTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EtsyShopManager/0.1");
            })
            .AddHttpMessageHandler<EtsyApiKeyHandler>()
            .AddHttpMessageHandler<EtsyRetryHandler>()
            .AddHttpMessageHandler<EtsyRateLimitHandler>();

        services.AddSingleton<EtsyOAuthClient>();
        services.AddSingleton<EtsyTokenService>();
        services.AddSingleton<EtsyApiClient>();
        return services;
    }
}
