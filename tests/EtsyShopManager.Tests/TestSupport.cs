using System.Net;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Etsy;

namespace EtsyShopManager.Tests;

/// <summary>TimeProvider whose timers fire immediately, so retry/back-off code runs without real waits.</summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (Delays) Delays.Add(dueTime);
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

/// <summary>Scripted HTTP responses; records every request.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var response = respond(request, Requests.Count);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class FakeCredentials : IEtsyCredentialsProvider
{
    public Task<EtsyCredentials?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult<EtsyCredentials?>(new EtsyCredentials("keystring123", "secret456"));
}

internal sealed class InMemoryTokenStore : IEtsyTokenStore
{
    public Dictionary<int, EtsyTokenSet> Tokens { get; } = [];
    public List<(int ShopId, string Reason)> Reauth { get; } = [];

    public Task<EtsyTokenSet?> GetAsync(int shopId, CancellationToken ct = default) =>
        Task.FromResult(Tokens.TryGetValue(shopId, out var t) ? t : null);

    public Task SaveAsync(int shopId, EtsyTokenSet tokens, CancellationToken ct = default)
    {
        Tokens[shopId] = tokens with { NeedsReauth = false };
        return Task.CompletedTask;
    }

    public Task MarkReauthRequiredAsync(int shopId, string reason, CancellationToken ct = default)
    {
        Reauth.Add((shopId, reason));
        if (Tokens.TryGetValue(shopId, out var t)) Tokens[shopId] = t with { NeedsReauth = true };
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPublisher : IDomainEventPublisher
{
    public List<IDomainEvent> Events { get; } = [];

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : IDomainEvent
    {
        Events.Add(domainEvent);
        return Task.CompletedTask;
    }
}

internal sealed class NullAuditLog : EtsyShopManager.Core.Services.IAuditLog
{
    public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
        string? userName = null, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Records enqueued jobs instead of running them (tests run handlers directly).</summary>
internal sealed class RecordingJobQueue : EtsyShopManager.Core.Jobs.IJobQueue
{
    public List<EtsyShopManager.Core.Jobs.JobRequest> Requests { get; } = [];

    public Task<long?> EnqueueAsync(EtsyShopManager.Core.Jobs.JobRequest request, CancellationToken ct = default)
    {
        if (request.DedupeKey is { } key && Requests.Any(r => r.DedupeKey == key)) return Task.FromResult<long?>(null);
        Requests.Add(request);
        return Task.FromResult<long?>(Requests.Count);
    }

    public Task<bool> RetryAsync(long jobId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> CancelAsync(long jobId, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>App settings kept in memory.</summary>
internal sealed class InMemorySettings : EtsyShopManager.Core.Services.ISettingsStore
{
    public Dictionary<string, string?> Values { get; } = [];
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> HasValueAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.ContainsKey(key));
}
