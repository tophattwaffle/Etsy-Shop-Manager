namespace EtsyShopManager.Etsy;

/// <summary>Static Etsy API settings (appsettings.json "Etsy" section). Keys/secrets live in the DB, not here.</summary>
public sealed class EtsyOptions
{
    public const string SectionName = "Etsy";

    public string ApiBaseUrl { get; set; } = "https://api.etsy.com";
    public string AuthorizeUrl { get; set; } = "https://www.etsy.com/oauth/connect";
    public string TokenUrl { get; set; } = "https://api.etsy.com/v3/public/oauth/token";

    /// <summary>Space-separated OAuth scopes requested when a shop is connected.</summary>
    public string Scopes { get; set; } = "listings_r listings_w listings_d shops_r shops_w transactions_r profile_r email_r";

    /// <summary>
    /// Requests per second across both shops (the limit is per API key). Evenly spaced calls at 5/s
    /// still drew 429s on bulk pushes (Oct 2026), so the default is 4; the limiter also slows down on its own after a 429.
    /// </summary>
    public int RequestsPerSecond { get; set; } = 4;

    /// <summary>Calls that may go back to back after idle time (1 = always evenly spaced).</summary>
    public int BurstSize { get; set; } = 1;

    /// <summary>The slowest spacing a run of 429s can push the pace to.</summary>
    public TimeSpan MaxRequestInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Successful calls after which a slowed-down pace speeds up one step again.</summary>
    public int RecoverAfterSuccesses { get; set; } = 20;

    /// <summary>Retries for 429/5xx/network errors before giving up.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Waits longer than this are not done inline; the job is rescheduled instead.</summary>
    public TimeSpan MaxInlineRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Refresh the access token when it expires within this window.</summary>
    public TimeSpan AccessTokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(100);
}
