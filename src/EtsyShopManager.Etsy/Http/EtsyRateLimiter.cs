using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy.Http;

/// <summary>
/// One token bucket for the whole app: Etsy limits are per API key, and both shops share the key.
/// </summary>
public sealed class EtsyRateLimiter : IAsyncDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public EtsyRateLimiter(IOptions<EtsyOptions> options)
    {
        var o = options.Value;
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            // One token every 1/RPS seconds (not RPS tokens once a second), so requests are spread evenly.
            // Refilling the whole bucket at once let a burst straddle a second boundary and drew 429s from Etsy.
            TokenLimit = Math.Max(1, o.BurstSize),
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1.0 / Math.Max(1, o.RequestsPerSecond)),
            QueueLimit = 100_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken ct)
    {
        var lease = await _limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            throw new EtsyRateLimitedException(TimeSpan.FromSeconds(5));
        }
        return lease;
    }

    public long QueuedRequests => _limiter.GetStatistics()?.CurrentQueuedCount ?? 0;

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}

/// <summary>Remembers the daily quota Etsy reports in response headers, for display and back-off.</summary>
public sealed class EtsyQuotaTracker(TimeProvider time)
{
    public int? LimitPerDay { get; private set; }
    public int? RemainingToday { get; private set; }
    public int? LimitPerSecond { get; private set; }
    public DateTime? LastUpdatedUtc { get; private set; }
    public long TotalRequests => Interlocked.Read(ref _total);
    public long Total429s => Interlocked.Read(ref _throttled);

    private long _total;
    private long _throttled;

    internal void Record(HttpResponseMessage response)
    {
        Interlocked.Increment(ref _total);
        if ((int)response.StatusCode == 429) Interlocked.Increment(ref _throttled);

        if (TryInt(response, "x-limit-per-day") is { } lpd) LimitPerDay = lpd;
        if (TryInt(response, "x-remaining-today") is { } rt) RemainingToday = rt;
        if (TryInt(response, "x-limit-per-second") is { } lps) LimitPerSecond = lps;
        LastUpdatedUtc = time.GetUtcNow().UtcDateTime;
    }

    private static int? TryInt(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var v) ? v : null;
}
