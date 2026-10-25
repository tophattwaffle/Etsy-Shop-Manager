using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class JobActivityTests
{
    /// <summary>Hands the subscription to the test, so changes are delivered one batch at a time on the test's thread.</summary>
    private sealed class ManualJobEvents : IJobEvents
    {
        public Func<IReadOnlyList<JobChanged>, Task>? Deliver { get; private set; }
        public void Publish(JobChanged change) { }

        public IDisposable Subscribe(Func<JobChanged, bool> filter, Func<IReadOnlyList<JobChanged>, Task> onChange, TimeSpan? debounce = null)
        {
            Deliver = onChange;
            return new Noop();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static (TestDb Db, ManualJobEvents Events, JobActivity Activity, EfJobQueue Queue, ServiceProvider Provider) Build()
    {
        var db = new TestDb();
        var events = new ManualJobEvents();
        var provider = new ServiceCollection().BuildServiceProvider();
        var catalog = new JobCatalog(provider.GetRequiredService<IServiceScopeFactory>());
        var queue = new EfJobQueue(db, new JobSignal(), TimeProvider.System, events, catalog);
        var activity = new JobActivity(db, events, catalog, NullLogger<JobActivity>.Instance);
        return (db, events, activity, queue, provider);
    }

    private static async Task<JobChanged> FinishAsync(TestDb db, long id)
    {
        await using var ctx = db.CreateDbContext();
        await ctx.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Succeeded));
        var job = await ctx.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
        return new JobChanged(id, job.JobType, job.JobType, job.ShopId, JobStatus.Succeeded, job.CreatedBy, null, null);
    }

    [Fact]
    public async Task Lists_queued_and_running_jobs_for_everyone_with_progress_per_run()
    {
        var (db, events, activity, queue, provider) = Build();
        using var _ = db;
        await using var __ = provider;

        var pushes = new List<long>();
        for (var i = 0; i < 3; i++)
            pushes.Add((await queue.EnqueueAsync(new JobRequest { JobType = "etsy.listing.push", ShopId = 1, CreatedBy = "tophattwaffle", MaxAttempts = 1 }))!.Value);
        await queue.EnqueueAsync(new JobRequest { JobType = "etsy.orders.poll", CreatedBy = "scheduler" });

        await activity.EnsureLoadedAsync();
        Assert.Equal(4, activity.Count);
        var push = Assert.Single(activity.Runs, r => r.JobType == "etsy.listing.push");
        Assert.Equal((0, 3), (push.Done, push.Total));
        Assert.Equal("tophattwaffle", push.CreatedBy);
        Assert.Single(activity.Runs, r => r.JobType == "etsy.orders.poll");

        // One push finishes: the run counts it as done, the other two stay listed.
        var changed = 0;
        activity.Changed += () => changed++;
        await events.Deliver!([await FinishAsync(db, pushes[0])]);
        push = Assert.Single(activity.Runs, r => r.JobType == "etsy.listing.push");
        Assert.Equal((1, 3), (push.Done, push.Total));
        Assert.Equal(2, push.Jobs.Count);
        Assert.Equal(3, activity.Count);
        Assert.Equal(1, changed);

        // Everything finishes: nothing to show, and the next push starts a new count.
        await events.Deliver!([await FinishAsync(db, pushes[1]), await FinishAsync(db, pushes[2])]);
        var poll = activity.Runs.Single().Jobs.Single();
        await events.Deliver!([await FinishAsync(db, poll.Id)]);
        Assert.Equal(0, activity.Count);
        Assert.Empty(activity.Runs);

        await queue.EnqueueAsync(new JobRequest { JobType = "etsy.listing.push", CreatedBy = "TopHATTwaffle" });
        await events.Deliver!([]);
        push = Assert.Single(activity.Runs);
        Assert.Equal((0, 1), (push.Done, push.Total));
    }
}
