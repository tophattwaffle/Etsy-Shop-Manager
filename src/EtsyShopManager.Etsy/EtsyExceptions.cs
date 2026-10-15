using System.Net;
using EtsyShopManager.Core.Jobs;

namespace EtsyShopManager.Etsy;

public class EtsyApiException(HttpStatusCode statusCode, string? error, string? body, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Error { get; } = error;
    public string? ResponseBody { get; } = body;
}

/// <summary>OAuth token endpoint error, e.g. invalid_grant.</summary>
public sealed class EtsyOAuthException(HttpStatusCode statusCode, string? error, string? description, string? body)
    : EtsyApiException(statusCode, error, body, $"Etsy OAuth error {(int)statusCode}: {error} {description}".Trim())
{
    public string? Description { get; } = description;
}

/// <summary>The shop's refresh token is no longer valid. The shop must be reconnected.</summary>
public sealed class EtsyReauthRequiredException(int shopId, string reason)
    : Exception($"Shop {shopId} must be reconnected to Etsy: {reason}")
{
    public int ShopId { get; } = shopId;
}

/// <summary>The Etsy app keystring/shared secret have not been configured yet.</summary>
public sealed class EtsyNotConfiguredException()
    : InvalidOperationException("Etsy app keystring and shared secret are not configured (Admin → Settings).");

/// <summary>Rate limited for longer than we are willing to wait inline.</summary>
public sealed class EtsyRateLimitedException(TimeSpan retryAfter)
    : RetryLaterException($"Etsy rate limit reached; retry after {retryAfter}.", retryAfter);
