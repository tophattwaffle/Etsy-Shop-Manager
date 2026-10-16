using EtsyShopManager.Core.Jobs;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

/// <summary>
/// The in-process job feed. The job queue and processor publish; Blazor pages subscribe and refresh themselves.
/// Subscribers are called on a timer thread, so components wrap their work in InvokeAsync.
/// </summary>
public sealed class JobEvents(TimeProvider time, ILogger<JobEvents> logger) : IJobEvents
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    private TimeProvider Time => time;
    private ILogger Logger => logger;

    public void Publish(JobChanged change)
    {
        Subscription[] subscribers;
        lock (_gate) subscribers = [.. _subscriptions];
        foreach (var s in subscribers) s.Offer(change);
    }

    public IDisposable Subscribe(Func<JobChanged, bool> filter, Func<IReadOnlyList<JobChanged>, Task> onChange,
        TimeSpan? debounce = null)
    {
        var subscription = new Subscription(this, filter, onChange, debounce ?? DefaultDebounce);
        lock (_gate) _subscriptions.Add(subscription);
        return subscription;
    }

    private void Remove(Subscription s)
    {
        lock (_gate) _subscriptions.Remove(s);
    }

    private sealed class Subscription(
        JobEvents owner,
        Func<JobChanged, bool> filter,
        Func<IReadOnlyList<JobChanged>, Task> onChange,
        TimeSpan debounce) : IDisposable
    {
        private readonly object _lock = new();
        private List<JobChanged> _pending = [];
        private ITimer? _timer;
        private bool _disposed;

        public void Offer(JobChanged change)
        {
            try
            {
                if (!filter(change)) return;
            }
            catch (Exception ex)
            {
                owner.Logger.LogWarning(ex, "Job event filter failed");
                return;
            }

            lock (_lock)
            {
                if (_disposed) return;
                _pending.Add(change);
                // The first change of a burst starts the window; later ones join it.
                _timer ??= owner.Time.CreateTimer(_ => _ = FlushAsync(), null, debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private async Task FlushAsync()
        {
            List<JobChanged> batch;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed || _pending.Count == 0) return;
                batch = _pending;
                _pending = [];
            }

            try
            {
                await onChange(batch);
            }
            catch (ObjectDisposedException)
            {
                // The page (circuit) went away between the event and the refresh.
            }
            catch (Exception ex)
            {
                owner.Logger.LogWarning(ex, "Job event subscriber failed");
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                _pending = [];
            }
            owner.Remove(this);
        }
    }
}

/// <summary>Display names of the registered job types (handlers are scoped, so they are read once from a scope).</summary>
public sealed class JobCatalog(IServiceScopeFactory scopeFactory)
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _names = new(() =>
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetServices<IJobHandler>()
            .GroupBy(h => h.JobType)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);
    });

    public string DisplayName(string jobType) => _names.Value.TryGetValue(jobType, out var name) ? name : jobType;

    public JobChanged Change(Job job, JobStatus status, string? result = null, string? error = null) =>
        new(job.Id, job.JobType, DisplayName(job.JobType), job.ShopId, status, job.CreatedBy, result, error);
}
