using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

/// <summary>
/// Enqueues each <see cref="RecurringJobDefinition"/> when its interval has elapsed. Last-run times are stored
/// in the database, so a restart does not re-run everything at once.
/// </summary>
public sealed class RecurringJobScheduler(
    IEnumerable<RecurringJobDefinition> definitions,
    ISettingsStore settings,
    IDbContextFactory<AppDbContext> dbFactory,
    IJobQueue queue,
    TimeProvider time,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            do
            {
                try
                {
                    await ScheduleDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Recurring job scheduler error");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Enqueues a recurring job now (the "Run now" button). Returns null if one is already queued.</summary>
    public static Task<long?> RunNowAsync(IJobQueue queue, RecurringJobDefinition def, string? user, CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = def.JobType,
            Payload = def.Payload,
            DedupeKey = $"recurring:{def.Name}",
            CreatedBy = user,
        }, ct);

    /// <summary>The definition's interval, or the value of its interval setting when one is set.</summary>
    public static async Task<TimeSpan> GetIntervalAsync(RecurringJobDefinition def, ISettingsStore settings,
        CancellationToken ct = default) =>
        def.IntervalSettingKey is null ? def.Interval : def.EffectiveInterval(await settings.GetAsync(def.IntervalSettingKey, ct));

    private async Task ScheduleDueAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        foreach (var def in definitions)
        {
            var state = await db.RecurringJobs.FindAsync([def.Name], ct);
            if (state is null)
            {
                // First time we see this definition: make it due after InitialDelay.
                state = new RecurringJobState { Name = def.Name, LastEnqueuedAtUtc = now - def.Interval + def.InitialDelay };
                db.RecurringJobs.Add(state);
                await db.SaveChangesAsync(ct);
            }

            var interval = await GetIntervalAsync(def, settings, ct);
            if (state.LastEnqueuedAtUtc + interval > now) continue;

            var id = await queue.EnqueueAsync(new JobRequest
            {
                JobType = def.JobType,
                Payload = def.Payload,
                DedupeKey = $"recurring:{def.Name}",
                CreatedBy = "scheduler",
            }, ct);

            state.LastEnqueuedAtUtc = now;
            if (id is not null) state.LastJobId = id;
            await db.SaveChangesAsync(ct);
            logger.LogDebug("Recurring job {Name} enqueued as {JobId}", def.Name, id);
        }
    }
}
