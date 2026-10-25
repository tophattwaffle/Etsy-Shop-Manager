using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

/// <summary>A queued or running job, as the title-bar jobs indicator shows it.</summary>
public sealed record ActiveJob(
    long Id,
    string JobType,
    string DisplayName,
    int? ShopId,
    JobStatus Status,
    string? CreatedBy,
    DateTime CreatedAtUtc,
    DateTime RunAfterUtc,
    DateTime? StartedAtUtc);

/// <summary>
/// Jobs of one type started by one person that are still going, e.g. a bulk push of 40 listings: how many of the run
/// are done. A run starts when its first job is queued and ends when none of its jobs is left.
/// </summary>
public sealed record JobRun(string JobType, string DisplayName, string? CreatedBy, int Done, int Total, IReadOnlyList<ActiveJob> Jobs)
{
    public ActiveJob? Running => Jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
}

/// <summary>
/// What the job queue is doing right now, for everyone signed in: every queued and running job, grouped into runs with
/// progress. One instance for the app; it follows <see cref="IJobEvents"/> and re-reads the queue after each burst
/// of changes, so every open page sees the same list.
/// </summary>
public sealed class JobActivity : IDisposable
{
    /// <summary>The most jobs read for the list; the count is always exact.</summary>
    public const int MaxListed = 200;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly JobCatalog _catalog;
    private readonly ILogger<JobActivity> _logger;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<(string JobType, string? CreatedBy), int> _done = [];
    private Task? _loaded;

    private IReadOnlyList<JobRun> _runs = [];
    private int _count;

    public JobActivity(IDbContextFactory<AppDbContext> dbFactory, IJobEvents events, JobCatalog catalog, ILogger<JobActivity> logger)
    {
        _dbFactory = dbFactory;
        _catalog = catalog;
        _logger = logger;
        _subscription = events.Subscribe(_ => true, OnChangesAsync, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>Raised after the list changed (on a background thread: components use InvokeAsync).</summary>
    public event Action? Changed;

    /// <summary>Queued and running jobs, in runs: running first, then in the order they will run.</summary>
    public IReadOnlyList<JobRun> Runs { get { lock (_gate) return _runs; } }

    /// <summary>How many jobs are queued or running.</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Reads the queue the first time it is needed (later changes arrive through job events).</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_gate) return _loaded ??= RefreshAsync();
    }

    private async Task OnChangesAsync(IReadOnlyList<JobChanged> changes)
    {
        lock (_gate)
        {
            foreach (var c in changes.Where(c => c.IsFinal))
            {
                var key = (c.JobType, Who(c.CreatedBy));
                _done[key] = _done.GetValueOrDefault(key) + 1;
            }
        }
        await RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var active = db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running);
            var count = await active.CountAsync();
            var jobs = (await active
                    .OrderByDescending(j => j.Status == JobStatus.Running)
                    .ThenByDescending(j => j.Priority).ThenBy(j => j.RunAfterUtc).ThenBy(j => j.Id)
                    .Take(MaxListed)
                    .ToListAsync())
                .Select(j => new ActiveJob(j.Id, j.JobType, _catalog.DisplayName(j.JobType), j.ShopId, j.Status,
                    j.CreatedBy, j.CreatedAtUtc, j.RunAfterUtc, j.StartedAtUtc))
                .ToList();

            lock (_gate)
            {
                var runs = jobs
                    .GroupBy(j => (j.JobType, CreatedBy: Who(j.CreatedBy)))
                    .Select(g =>
                    {
                        var done = _done.GetValueOrDefault(g.Key);
                        return new JobRun(g.Key.JobType, g.First().DisplayName, g.First().CreatedBy, done, done + g.Count(), [.. g]);
                    })
                    .ToList();
                // A run that has nothing left is over: its next job starts a new count.
                foreach (var key in _done.Keys.Where(k => !runs.Any(r => (r.JobType, Who(r.CreatedBy)) == k)).ToList())
                    _done.Remove(key);
                _runs = runs;
                _count = count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the job queue for the jobs indicator");
            return;
        }
        finally
        {
            _refreshGate.Release();
        }
        Changed?.Invoke();
    }

    private static string? Who(string? createdBy) => createdBy?.ToLowerInvariant();

    public void Dispose() => _subscription.Dispose();
}
