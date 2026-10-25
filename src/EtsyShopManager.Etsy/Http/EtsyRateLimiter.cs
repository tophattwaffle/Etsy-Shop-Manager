using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy.Http;

/// <summary>
/// One pacer for the whole app: Etsy limits are per API key, and both shops (bulk pushes, order polls, refills, imports)
/// share the key, so every call to the application API takes a slot here. Slots are spaced evenly (one every
/// 1/RPS seconds); bursting drew 429s from Etsy. It adapts: a 429 pauses everyone for Etsy's Retry-After and doubles
/// the spacing (up to <see cref="EtsyOptions.MaxRequestInterval"/>), and each run of
/// <see cref="EtsyOptions.RecoverAfterSuccesses"/> successful calls brings it back a step towards the configured rate.
/// </summary>
public sealed class EtsyRateLimiter
{
    private readonly EtsyOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private TimeSpan _interval;
    private TimeSpan _baseInterval;
    private int _successes;
    private long _queued;

    public EtsyRateLimiter(IOptions<EtsyOptions> options, TimeProvider time, ILogger<EtsyRateLimiter>? logger = null)
    {
        _options = options.Value;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _baseInterval = _interval = IntervalFor(_options.RequestsPerSecond);
    }

    /// <summary>Requests waiting for a slot right now.</summary>
    public long QueuedRequests => Interlocked.Read(ref _queued);

    /// <summary>The pace in use now: lower than configured after recent 429s.</summary>
    public double CurrentRequestsPerSecond
    {
        get { lock (_gate) return 1 / _interval.TotalSeconds; }
    }

    /// <summary>When all calls are paused after a 429 (null when not paused).</summary>
    public DateTimeOffset? PausedUntil
    {
        get { lock (_gate) return _pausedUntil > _time.GetUtcNow() ? _pausedUntil : null; }
    }

    /// <summary>Waits for this caller's slot. A pause that starts while waiting (another call got a 429) is honoured.</summary>
    public async ValueTask AcquireAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _queued);
        try
        {
            // A caller waits out a pause at most once, then takes a slot (so a clock that stands still can't trap it).
            var waitedPause = false;
            while (true)
            {
                TimeSpan wait;
                bool haveSlot;
                lock (_gate)
                {
                    var now = _time.GetUtcNow();
                    haveSlot = waitedPause || _pausedUntil <= now;
                    if (!haveSlot)
                    {
                        wait = _pausedUntil - now;
                    }
                    else
                    {
                        // A short burst is allowed after idle time, never more than BurstSize calls back to back.
                        var earliest = now - _interval * (Math.Max(1, _options.BurstSize) - 1);
                        var slot = _nextSlot > earliest ? _nextSlot : earliest;
                        _nextSlot = slot + _interval;
                        wait = slot > now ? slot - now : TimeSpan.Zero;
                        if (wait == TimeSpan.Zero) return;
                    }
                }
                await Task.Delay(wait, _time, ct);
                if (!haveSlot)
                {
                    waitedPause = true; // the pause is over: now take a slot
                    continue;
                }
                if (waitedPause) return;
                // The slot is ours, unless a 429 paused everyone while we waited for it.
                lock (_gate)
                {
                    if (_pausedUntil <= _time.GetUtcNow()) return;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>Feeds Etsy's answer back into the pace (called for every response, retries included).</summary>
    public void Observe(HttpResponseMessage response)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (Header(response, "x-limit-per-second") is { } limit && limit > 0)
            {
                // Stay a step under the limit Etsy reports, and never above the configured rate.
                var reported = IntervalFor(Math.Max(1, limit - 1));
                var configured = IntervalFor(_options.RequestsPerSecond);
                var newBase = reported > configured ? reported : configured;
                if (newBase != _baseInterval)
                {
                    _baseInterval = newBase;
                    if (_interval < _baseInterval) _interval = _baseInterval;
                }
            }

            if ((int)response.StatusCode == 429)
            {
                var pause = RetryAfter(response, now) ?? TimeSpan.FromSeconds(1);
                if (now + pause > _pausedUntil) _pausedUntil = now + pause;
                var slower = _interval * 2;
                var max = _options.MaxRequestInterval > _baseInterval ? _options.MaxRequestInterval : _baseInterval;
                var next = slower < max ? slower : max;
                if (next != _interval)
                    _logger.LogWarning("Etsy rate limit hit: pausing {Pause} and slowing to {Rate:0.##} requests/s",
                        pause, 1 / next.TotalSeconds);
                _interval = next;
                _successes = 0;
                return;
            }

            // Etsy says this second's allowance is used up: wait for the next one rather than draw a 429.
            if (Header(response, "x-remaining-this-second") == 0 && now + TimeSpan.FromSeconds(1) > _pausedUntil)
                _pausedUntil = now + TimeSpan.FromSeconds(1);

            if (_interval <= _baseInterval || ++_successes < Math.Max(1, _options.RecoverAfterSuccesses)) return;
            _successes = 0;
            var faster = _interval * 0.75;
            _interval = faster > _baseInterval ? faster : _baseInterval;
            if (_interval == _baseInterval)
                _logger.LogInformation("Etsy pace back to {Rate:0.##} requests/s", 1 / _interval.TotalSeconds);
        }
    }

    private static TimeSpan IntervalFor(int requestsPerSecond) => TimeSpan.FromSeconds(1.0 / Math.Max(1, requestsPerSecond));

    internal static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }

    private static int? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var v) ? v : null;
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
