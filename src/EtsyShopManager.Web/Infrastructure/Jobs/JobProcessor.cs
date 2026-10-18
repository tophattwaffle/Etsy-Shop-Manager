using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

/// <summary>
/// Runs queued jobs one at a time (Etsy's per-key rate limit is the bottleneck anyway, and SQLite has a
/// single writer). On startup, jobs left Running by a crash or restart go back to Pending.
/// </summary>
public sealed class JobProcessor(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IJobSignal signal,
    IJobEvents events,
    JobCatalog catalog,
    TimeProvider time,
    IOptions<JobOptions> options,
    ILogger<JobProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (and the DB finish migrating) before touching the queue.
        await Task.Yield();
        if (!await StartUpAsync(stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimNextAsync(stoppingToken);
                if (job is null)
                {
                    await signal.WaitAsync(options.Value.PollInterval, stoppingToken);
                    continue;
                }
                await RunAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job processor loop error");
                try { await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Re-queues interrupted jobs, retrying until it works: an exception escaping ExecuteAsync would end job processing
    /// for good (and by default stop the whole service), so a busy or locked database here only delays the start.
    /// False when the service is stopping.
    /// </summary>
    private async Task<bool> StartUpAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                await RecoverInterruptedJobsAsync(stoppingToken);
                LogRegisteredHandlers();
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job processor start-up failed; retrying in 5 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken); }
                catch (OperationCanceledException) { return false; }
            }
        }
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var recovered = await db.Jobs
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.RunAfterUtc, now)
                .SetProperty(j => j.LastError, "Interrupted by restart"), ct);
        if (recovered > 0) logger.LogWarning("Re-queued {Count} job(s) interrupted by a restart", recovered);
    }

    private void LogRegisteredHandlers()
    {
        using var scope = scopeFactory.CreateScope();
        var types = scope.ServiceProvider.GetServices<IJobHandler>().Select(h => h.JobType).ToList();
        var duplicates = types.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            logger.LogError("Duplicate job handlers registered for: {Types}", string.Join(", ", duplicates));
        logger.LogInformation("Job handlers: {Types}", string.Join(", ", types.Order()));
    }

    private async Task<Job?> ClaimNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        var candidate = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending && j.RunAfterUtc <= now)
            .OrderByDescending(j => j.Priority).ThenBy(j => j.RunAfterUtc).ThenBy(j => j.Id)
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return null;

        // Guard against a cancel from the UI between the read and the claim.
        var claimed = await db.Jobs
            .Where(j => j.Id == candidate.Id && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAtUtc, now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
        if (claimed == 0) return null;

        candidate.Status = JobStatus.Running;
        candidate.StartedAtUtc = now;
        candidate.Attempts += 1;
        events.Publish(catalog.Change(candidate, JobStatus.Running));
        return candidate;
    }

    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.JobType == job.JobType);
        if (handler is null)
        {
            await FinishAsync(job, JobStatus.Failed, $"No handler registered for job type '{job.JobType}'.");
            return;
        }

        var context = new JobExecutionContext
        {
            JobId = job.Id,
            JobType = job.JobType,
            Attempt = job.Attempts,
            ShopId = job.ShopId,
            PayloadJson = job.PayloadJson,
            CorrelationId = job.CorrelationId,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(options.Value.JobTimeout);

        logger.LogInformation("Running job {JobId} {JobType} (attempt {Attempt}/{Max})", job.Id, job.JobType, job.Attempts, job.MaxAttempts);
        try
        {
            using (EtsyCallContext.Begin($"job {job.Id} {job.JobType}")) // names the job in the Etsy write journal
                await handler.HandleAsync(context, timeout.Token);
            await FinishAsync(job, JobStatus.Succeeded, null, context.Result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: hand the job back without spending an attempt.
            await RescheduleAsync(job, TimeSpan.Zero, "Interrupted by shutdown", refundAttempt: true);
        }
        catch (RetryLaterException ex)
        {
            logger.LogInformation("Job {JobId} deferred for {Delay}: {Message}", job.Id, ex.Delay, ex.Message);
            await RescheduleAsync(job, ex.Delay, ex.Message, refundAttempt: true);
        }
        catch (Exception ex) when (ex is JobPermanentFailureException or EtsyReauthRequiredException or EtsyNotConfiguredException)
        {
            logger.LogWarning("Job {JobId} {JobType} failed permanently: {Message}", job.Id, job.JobType, ex.Message);
            await FinishAsync(job, JobStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            var timedOut = ex is OperationCanceledException && timeout.IsCancellationRequested;
            var message = timedOut ? $"Timed out after {options.Value.JobTimeout}" : ex.ToString();

            if (job.Attempts < job.MaxAttempts)
            {
                var delay = Backoff(job.Attempts);
                logger.LogWarning(ex, "Job {JobId} {JobType} failed (attempt {Attempt}); retrying in {Delay}", job.Id, job.JobType, job.Attempts, delay);
                await RescheduleAsync(job, delay, message, refundAttempt: false);
            }
            else
            {
                logger.LogError(ex, "Job {JobId} {JobType} failed after {Attempts} attempts", job.Id, job.JobType, job.Attempts);
                await FinishAsync(job, JobStatus.Failed, message);
            }
        }
    }

    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attempt - 1))));

    // These writes use CancellationToken.None so the outcome is recorded even during shutdown.
    private async Task FinishAsync(Job job, JobStatus status, string? error, string? result = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var now = time.GetUtcNow().UtcDateTime;
        var summary = Truncate(result, 500);
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.CompletedAtUtc, now)
            .SetProperty(j => j.LastError, Truncate(error))
            .SetProperty(j => j.Result, summary), CancellationToken.None);
        events.Publish(catalog.Change(job, status, summary, FirstLine(error)));
    }

    private async Task RescheduleAsync(Job job, TimeSpan delay, string? error, bool refundAttempt)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var runAfter = time.GetUtcNow().UtcDateTime + delay;
        var refund = refundAttempt ? 1 : 0;
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Pending)
            .SetProperty(j => j.RunAfterUtc, runAfter)
            .SetProperty(j => j.Attempts, j => j.Attempts - refund)
            .SetProperty(j => j.LastError, Truncate(error)), CancellationToken.None);
        events.Publish(catalog.Change(job, JobStatus.Pending, error: FirstLine(error)));
    }

    private static string? FirstLine(string? s) => s?.Split('\n', 2)[0].Trim();

    private static string? Truncate(string? s, int max = 4000) => s is { Length: var n } && n > max ? s[..max] : s;
}
