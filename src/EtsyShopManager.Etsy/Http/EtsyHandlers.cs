using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy.Http;

/// <summary>Adds "x-api-key: keystring:shared_secret" to Open API v3 application calls.</summary>
internal sealed class EtsyApiKeyHandler(IEtsyCredentialsProvider credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath.StartsWith("/v3/application", StringComparison.OrdinalIgnoreCase) == true)
        {
            var creds = await credentials.GetAsync(ct) ?? throw new EtsyNotConfiguredException();
            request.Headers.Remove("x-api-key");
            request.Headers.TryAddWithoutValidation("x-api-key", creds.ApiKeyHeaderValue);
        }
        return await base.SendAsync(request, ct);
    }
}

/// <summary>
/// Retries 429 (any method) and 5xx / network errors (idempotent methods only), honouring Retry-After.
/// Waits longer than <see cref="EtsyOptions.MaxInlineRetryDelay"/> are returned to the caller, which turns
/// a 429 into <see cref="EtsyRateLimitedException"/> so the job is rescheduled instead of blocking a worker.
/// </summary>
internal sealed class EtsyRetryHandler(IOptions<EtsyOptions> options, TimeProvider time, ILogger<EtsyRetryHandler> logger)
    : DelegatingHandler
{
    /// <summary>Set on a request to disable retries (e.g. a non-repeatable upload stream).</summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("Etsy.NoRetry");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var o = options.Value;
        var maxRetries = request.Options.TryGetValue(NoRetry, out var noRetry) && noRetry ? 0 : o.MaxRetries;
        var idempotent = IsIdempotent(request.Method);

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && idempotent && attempt < maxRetries)
            {
                var wait = Backoff(attempt);
                logger.LogWarning(ex, "Etsy {Method} {Path} failed (attempt {Attempt}); retrying in {Delay}",
                    request.Method, request.RequestUri?.AbsolutePath, attempt + 1, wait);
                await Task.Delay(wait, time, ct);
                continue;
            }

            if (attempt >= maxRetries || !ShouldRetry(response.StatusCode, idempotent))
                return response;

            var delay = RetryAfter(response) ?? Backoff(attempt);
            if (delay > o.MaxInlineRetryDelay)
                return response;

            logger.LogInformation("Etsy {Method} {Path} returned {Status}; retry {Attempt} in {Delay}",
                request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, attempt + 1, delay);
            response.Dispose();
            await Task.Delay(delay, time, ct);
        }
    }

    internal static bool ShouldRetry(HttpStatusCode status, bool idempotent) =>
        status == HttpStatusCode.TooManyRequests
        || (idempotent && status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);

    internal static bool IsIdempotent(HttpMethod m) =>
        m == HttpMethod.Get || m == HttpMethod.Head || m == HttpMethod.Put || m == HttpMethod.Delete
        || m == HttpMethod.Options || m == HttpMethod.Patch;

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(16_000, 1000 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 250));

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date)
        {
            var d = date - time.GetUtcNow();
            return d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }
        return null;
    }
}

/// <summary>Takes a token from the shared bucket before every attempt (so retries are rate limited too).</summary>
internal sealed class EtsyRateLimitHandler(EtsyRateLimiter limiter, EtsyQuotaTracker quota) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var lease = await limiter.AcquireAsync(ct);
        var response = await base.SendAsync(request, ct);
        quota.Record(response);
        return response;
    }
}
