using System.Net;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

public class EtsyRateLimiterTests
{
    /// <summary>A clock that moves forward by every delay it is asked for, as real time would for one caller.</summary>
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        public DateTimeOffset Start { get; } = new(2026, 10, 24, 21, 50, 0, TimeSpan.Zero);
        private DateTimeOffset _now;

        public AdvancingTimeProvider() => _now = Start;

        public TimeSpan Elapsed { get { lock (_gate) return _now - Start; } }

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public void Advance(TimeSpan by) { lock (_gate) _now += by; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    private static EtsyRateLimiter Limiter(AdvancingTimeProvider time, int rps = 4, int burst = 1) =>
        new(Options.Create(new EtsyOptions { RequestsPerSecond = rps, BurstSize = burst }), time);

    private static HttpResponseMessage TooMany(int retryAfterSeconds = 1)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return r;
    }

    [Fact]
    public async Task Calls_are_spaced_evenly_at_the_configured_rate()
    {
        var time = new AdvancingTimeProvider();
        var limiter = Limiter(time);

        for (var i = 0; i < 5; i++) await limiter.AcquireAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed); // first now, then one every 250 ms
    }

    [Fact]
    public async Task A_429_pauses_every_caller_and_halves_the_pace()
    {
        var time = new AdvancingTimeProvider();
        var limiter = Limiter(time);
        await limiter.AcquireAsync(CancellationToken.None);

        limiter.Observe(TooMany(retryAfterSeconds: 3));
        Assert.Equal(2, limiter.CurrentRequestsPerSecond, 3);
        Assert.NotNull(limiter.PausedUntil);

        var before = time.Elapsed;
        await limiter.AcquireAsync(CancellationToken.None);
        Assert.True(time.Elapsed - before >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Repeated_429s_slow_down_to_the_floor_then_successes_bring_the_pace_back_gradually()
    {
        var time = new AdvancingTimeProvider();
        var limiter = Limiter(time);

        for (var i = 0; i < 5; i++) limiter.Observe(TooMany());
        Assert.Equal(0.5, limiter.CurrentRequestsPerSecond, 3); // never slower than one call every 2 s

        for (var i = 0; i < 19; i++) limiter.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(0.5, limiter.CurrentRequestsPerSecond, 3); // not yet: recovery needs a run of 20
        limiter.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(limiter.CurrentRequestsPerSecond is > 0.5 and < 4);

        for (var i = 0; i < 400; i++) limiter.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(4, limiter.CurrentRequestsPerSecond, 3); // and never faster than configured
    }

    [Fact]
    public async Task No_allowance_left_this_second_waits_for_the_next_second()
    {
        var time = new AdvancingTimeProvider();
        var limiter = Limiter(time);
        await limiter.AcquireAsync(CancellationToken.None);

        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        ok.Headers.TryAddWithoutValidation("x-remaining-this-second", "0");
        limiter.Observe(ok);

        var before = time.Elapsed;
        await limiter.AcquireAsync(CancellationToken.None);
        Assert.True(time.Elapsed - before >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_lower_limit_reported_by_Etsy_lowers_the_pace_below_it()
    {
        var time = new AdvancingTimeProvider();
        var limiter = Limiter(time, rps: 10);

        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        ok.Headers.TryAddWithoutValidation("x-limit-per-second", "3");
        limiter.Observe(ok);

        Assert.Equal(2, limiter.CurrentRequestsPerSecond, 3);
    }

    /// <summary>
    /// Etsy as measured on 2026-10-23/24: more than its per-second allowance in any one-second window gets a 429 with
    /// Retry-After: 1, and the refused request has not been processed. A bulk push (reads, photo uploads, deletes)
    /// paced faster than the allowance must still finish every call, uploads included, and settle below the limit.
    /// </summary>
    [Fact]
    public async Task Bulk_run_against_a_strict_Etsy_finishes_every_call_and_stops_drawing_429s()
    {
        var time = new AdvancingTimeProvider();
        var sent = new List<DateTimeOffset>();
        var throttledAt = new List<int>();
        var etsy = new ScriptedHandler((_, n) =>
        {
            var now = time.GetUtcNow();
            lock (sent)
            {
                if (sent.Count(t => t > now - TimeSpan.FromSeconds(1)) >= 3)
                {
                    throttledAt.Add(n);
                    return TooMany();
                }
                sent.Add(now);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = Options.Create(new EtsyOptions { RequestsPerSecond = 5, BurstSize = 2, MaxRetries = 4 });
        var limiter = new EtsyRateLimiter(options, time);
        var rateLimit = new EtsyRateLimitHandler(limiter, new EtsyQuotaTracker(time)) { InnerHandler = etsy };
        var retry = new EtsyRetryHandler(options, time, NullLogger<EtsyRetryHandler>.Instance) { InnerHandler = rateLimit };
        var client = new HttpClient(retry) { BaseAddress = new Uri("https://api.etsy.com") };

        for (var i = 0; i < 60; i++)
        {
            using var request = i % 3 == 0
                ? new HttpRequestMessage(HttpMethod.Post, "/v3/application/shops/1/listings/2/images") { Content = new ByteArrayContent([1, 2, 3]) }
                : new HttpRequestMessage(HttpMethod.Get, "/v3/application/listings/2/images");
            if (request.Method == HttpMethod.Post) request.Options.Set(EtsyRetryHandler.NoRetry, true);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(60, sent.Count);
        Assert.NotEmpty(throttledAt);
        // Once it has slowed down it stays under Etsy's limit: no 429 in the second half of the run.
        Assert.DoesNotContain(throttledAt, n => n > etsy.Requests.Count / 2);
    }
}
