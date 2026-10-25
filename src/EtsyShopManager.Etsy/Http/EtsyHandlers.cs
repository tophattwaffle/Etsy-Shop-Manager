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
        // NoRetry only covers failures whose outcome is unknown (5xx, network). A 429 means Etsy refused the request
        // without processing it, so even a photo upload or a create is safe to send again.
        var noRetry = request.Options.TryGetValue(NoRetry, out var flag) && flag;
        var maxRetries = o.MaxRetries;
        var idempotent = IsIdempotent(request.Method) && !noRetry;

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

            // Etsy sends Retry-After: 1 on every 429; waiting at least the exponential back-off (1s, 2s, 4s, 8s…) keeps
            // repeated 429s from hammering it at the same 1s pace.
            var backoff = Backoff(attempt);
            var delay = RetryAfter(response) is { } ra && ra > backoff ? ra : backoff;
            if (delay > o.MaxInlineRetryDelay)
                return response;

            // Etsy's limit headers are logged so the pace can be checked against what Etsy actually allows.
            logger.LogInformation("Etsy {Method} {Path} returned {Status}; retry {Attempt} in {Delay} (Retry-After {RetryAfter}, limit/s {LimitPerSecond}, left this second {RemainingThisSecond}, left today {RemainingToday})",
                request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, attempt + 1, delay,
                response.Headers.RetryAfter?.ToString() ?? "-", Header(response, "x-limit-per-second"),
                Header(response, "x-remaining-this-second"), Header(response, "x-remaining-today"));
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

    /// <summary>1s, 2s, 4s, 8s, 16s, capped at 30s, plus up to 25% jitter so callers don't retry in step.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var ms = Math.Min(30_000, 1000 * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(ms + Random.Shared.NextDouble() * ms * 0.25);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => EtsyRateLimiter.RetryAfter(response, time.GetUtcNow());

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "-" : "-";
}

/// <summary>Takes a token from the shared bucket before every attempt (so retries are rate limited too).</summary>
internal sealed class EtsyRateLimitHandler(EtsyRateLimiter limiter, EtsyQuotaTracker quota) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await limiter.AcquireAsync(ct);
        var response = await base.SendAsync(request, ct);
        limiter.Observe(response);
        quota.Record(response);
        return response;
    }
}

/// <summary>
/// Records every write sent to Etsy's application API (each attempt, retries included) with Etsy's reply, through
/// <see cref="IEtsyWriteJournal"/>. Sits innermost, so what it records is exactly what went over the wire. OAuth
/// token calls are never recorded (their bodies hold refresh tokens), and headers (API key, bearer token) never are.
/// </summary>
internal sealed class EtsyWriteJournalHandler(IEtsyWriteJournal journal, TimeProvider time, ILogger<EtsyWriteJournalHandler> logger)
    : DelegatingHandler
{
    internal const int MaxBodyChars = 8_000;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsJournaled(request)) return await base.SendAsync(request, ct);

        var started = time.GetTimestamp();
        var at = time.GetUtcNow().UtcDateTime;
        var requestBody = await DescribeAsync(request.Content, ct);
        try
        {
            var response = await base.SendAsync(request, ct);
            string? responseBody = null;
            try
            {
                await response.Content.LoadIntoBufferAsync(ct); // so the caller can still read it
                responseBody = Cut(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                responseBody = $"<unreadable: {ex.Message}>";
            }
            await RecordAsync(Record(request, at, started, requestBody) with { StatusCode = (int)response.StatusCode, ResponseBody = responseBody });
            return response;
        }
        catch (Exception ex)
        {
            await RecordAsync(Record(request, at, started, requestBody) with { Error = ex.Message });
            throw;
        }
    }

    internal static bool IsJournaled(HttpRequestMessage request) =>
        request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options
        && request.RequestUri?.AbsolutePath is { } path
        && path.StartsWith("/v3/application", StringComparison.OrdinalIgnoreCase);

    private EtsyWriteRecord Record(HttpRequestMessage request, DateTime at, long started, string? body) => new()
    {
        AtUtc = at,
        Method = request.Method.Method,
        Path = request.RequestUri!.PathAndQuery,
        RequestBody = body,
        DurationMs = (long)time.GetElapsedTime(started).TotalMilliseconds,
        Source = EtsyCallContext.Source,
    };

    private async Task RecordAsync(EtsyWriteRecord record)
    {
        try
        {
            await journal.RecordAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record Etsy write {Method} {Path}", record.Method, record.Path);
        }
    }

    /// <summary>The body as sent: text as is, multipart part by part with files summarised (name and size only).</summary>
    internal static async Task<string?> DescribeAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null) return null;
        if (content is MultipartContent multipart)
        {
            var parts = new List<string>();
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "?";
                var fileName = part.Headers.ContentDisposition?.FileName?.Trim('"');
                if (fileName is not null || part is ByteArrayContent and not StringContent || part is StreamContent)
                {
                    await part.LoadIntoBufferAsync(ct);
                    parts.Add($"{name}=<file {fileName ?? "unnamed"}, {part.Headers.ContentLength ?? 0} bytes>");
                }
                else
                {
                    parts.Add($"{name}={await part.ReadAsStringAsync(ct)}");
                }
            }
            return Cut(string.Join("&", parts));
        }
        await content.LoadIntoBufferAsync(ct); // the send reuses the buffer
        return Cut(await content.ReadAsStringAsync(ct));
    }

    private static string Cut(string s) => s.Length <= MaxBodyChars ? s : s[..MaxBodyChars] + "…";
}
