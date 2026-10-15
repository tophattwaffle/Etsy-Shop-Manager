namespace EtsyShopManager.Etsy;

/// <summary>Static Etsy API settings (appsettings.json "Etsy" section). Keys/secrets live in the DB, not here.</summary>
public sealed class EtsyOptions
{
    public const string SectionName = "Etsy";

    public string ApiBaseUrl { get; set; } = "https://api.etsy.com";
    public string AuthorizeUrl { get; set; } = "https://www.etsy.com/oauth/connect";
    public string TokenUrl { get; set; } = "https://api.etsy.com/v3/public/oauth/token";

    /// <summary>Space-separated OAuth scopes requested when a shop is connected.</summary>
    public string Scopes { get; set; } = "listings_r listings_w listings_d shops_r shops_w transactions_r profile_r";

    /// <summary>Requests per second allowed across both shops (the limit is per API key).</summary>
    public int RequestsPerSecond { get; set; } = 5;

    /// <summary>Token bucket size (short bursts).</summary>
    public int BurstSize { get; set; } = 5;

    /// <summary>Retries for 429/5xx/network errors before giving up.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Waits longer than this are not done inline; the job is rescheduled instead.</summary>
    public TimeSpan MaxInlineRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Refresh the access token when it expires within this window.</summary>
    public TimeSpan AccessTokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(100);
}
