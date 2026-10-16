using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How often the processor looks for due jobs when nothing signals it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A single job is cancelled if it runs longer than this.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public int SucceededRetentionDays { get; set; } = 30;

    /// <summary>
    /// Succeeded jobs the scheduler queued (e.g. order polls every few minutes) are routine; keep them only this long
    /// so they don't bury everything else on the Jobs page. Failures are kept as long as any other.
    /// </summary>
    public int ScheduledSucceededRetentionHours { get; set; } = 24;
    public int FailedRetentionDays { get; set; } = 90;
}

public sealed class JobSignal : IJobSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            if (_semaphore.CurrentCount == 0) _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

/// <summary>Persistent job queue in SQLite.</summary>
public sealed class EfJobQueue(
    IDbContextFactory<AppDbContext> dbFactory,
    IJobSignal signal,
    TimeProvider time,
    IJobEvents events,
    JobCatalog catalog) : IJobQueue
{
    // Serialises the dedupe check + insert within this process (the only writer).
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);

    public async Task<long?> EnqueueAsync(JobRequest request, CancellationToken ct = default)
    {
        await _enqueueGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            if (request.DedupeKey is { } key &&
                await db.Jobs.AnyAsync(j => j.DedupeKey == key &&
                                            (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct))
            {
                return null;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var job = new Job
            {
                JobType = request.JobType,
                PayloadJson = request.Payload is null ? null : JsonSerializer.Serialize(request.Payload, JobJson.Options),
                Status = JobStatus.Pending,
                Priority = request.Priority,
                ShopId = request.ShopId,
                DedupeKey = request.DedupeKey,
                CorrelationId = request.CorrelationId,
                MaxAttempts = Math.Max(1, request.MaxAttempts),
                CreatedAtUtc = now,
                RunAfterUtc = request.RunAfterUtc ?? now,
                CreatedBy = request.CreatedBy,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            signal.Notify();
            events.Publish(catalog.Change(job, JobStatus.Pending));
            return job.Id;
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public async Task<bool> RetryAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.RunAfterUtc, now)
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)null)
                .SetProperty(j => j.Result, (string?)null), ct);
        if (updated > 0)
        {
            signal.Notify();
            await PublishAsync(db, jobId, JobStatus.Pending, ct);
        }
        return updated > 0;
    }

    public async Task<bool> CancelAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Cancelled)
                .SetProperty(j => j.CompletedAtUtc, now), ct);
        if (updated > 0) await PublishAsync(db, jobId, JobStatus.Cancelled, ct);
        return updated > 0;
    }

    private async Task PublishAsync(AppDbContext db, long jobId, JobStatus status, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is not null) events.Publish(catalog.Change(job, status));
    }
}

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddJobInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobOptions>(configuration.GetSection(JobOptions.SectionName));
        services.AddSingleton<IJobSignal, JobSignal>();
        services.AddSingleton<IJobEvents, JobEvents>();
        services.AddSingleton<JobCatalog>();
        services.AddSingleton<IJobQueue, EfJobQueue>();
        services.AddHostedService<JobProcessor>();
        services.AddHostedService<RecurringJobScheduler>();
        return services;
    }

    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services)
        where THandler : class, IJobHandler =>
        services.AddScoped<IJobHandler, THandler>();

    public static IServiceCollection AddRecurringJob(this IServiceCollection services, RecurringJobDefinition definition) =>
        services.AddSingleton(definition);
}
