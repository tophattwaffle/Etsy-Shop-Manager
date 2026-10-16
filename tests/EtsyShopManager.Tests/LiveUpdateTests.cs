using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Jobs;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

public class JobEventsTests
{
    private static JobChanged Change(long id, string type = "t", JobStatus status = JobStatus.Succeeded) =>
        new(id, type, type, null, status, "tophattwaffle", null, null);

    [Fact]
    public async Task Bursts_are_coalesced_filtered_and_stop_after_dispose()
    {
        var events = new JobEvents(TimeProvider.System, NullLogger<JobEvents>.Instance);
        var batches = new List<IReadOnlyList<JobChanged>>();
        var got = new SemaphoreSlim(0);
        var sub = events.Subscribe(c => c.JobType == "t", batch =>
        {
            lock (batches) batches.Add(batch);
            got.Release();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(100));

        events.Publish(Change(1));
        events.Publish(Change(2, type: "other")); // filtered out
        events.Publish(Change(3));
        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));

        var batch = Assert.Single(batches);
        Assert.Equal([1L, 3L], batch.Select(c => c.JobId));

        sub.Dispose();
        events.Publish(Change(4));
        Assert.False(await got.WaitAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task A_failing_subscriber_does_not_affect_others()
    {
        var events = new JobEvents(TimeProvider.System, NullLogger<JobEvents>.Instance);
        var got = new TaskCompletionSource();
        using var bad = events.Subscribe(_ => true, _ => throw new InvalidOperationException("boom"), TimeSpan.FromMilliseconds(10));
        using var good = events.Subscribe(_ => true, _ => { got.TrySetResult(); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(10));

        events.Publish(Change(1));

        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class JobStackTests
{
    private static Job J(long id, string type, JobStatus status = JobStatus.Succeeded) =>
        new() { Id = id, JobType = type, Status = status };

    [Fact]
    public void Runs_of_one_type_stack_a_different_type_breaks_them_and_failures_stand_alone()
    {
        var jobs = new[]
        {
            J(10, "poll"), J(9, "poll"), J(8, "poll", JobStatus.Failed), J(7, "poll"), J(6, "poll"),
            J(5, "import"), J(4, "poll"), J(3, "poll"),
        };

        var stacks = JobStacks.Group(jobs);

        Assert.Equal(
            ["10,9", "8", "7,6", "5", "4,3"],
            stacks.Select(s => string.Join(",", s.Jobs.Select(j => j.Id))));
        Assert.Equal(9, stacks[0].Key); // the oldest job: stays the same as newer polls join the stack
    }
}

public class JobProcessorEventTests
{
    private sealed class SampleJob : IJobHandler
    {
        public string JobType => "sample";
        public string DisplayName => "Sample job";

        public Task HandleAsync(JobExecutionContext context, CancellationToken ct)
        {
            context.Result = "3 things done";
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Processor_publishes_start_and_finish_with_the_handlers_result()
    {
        using var db = new TestDb();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddSingleton<IJobSignal, JobSignal>();
        services.AddSingleton<IJobEvents, JobEvents>();
        services.AddSingleton<JobCatalog>();
        services.AddSingleton<IJobQueue, EfJobQueue>();
        services.AddScoped<IJobHandler, SampleJob>();
        services.AddSingleton(Options.Create(new JobOptions { PollInterval = TimeSpan.FromMilliseconds(50) }));
        services.AddSingleton<JobProcessor>();
        await using var provider = services.BuildServiceProvider();

        var seen = new List<JobChanged>();
        var finished = new TaskCompletionSource<JobChanged>();
        using var sub = provider.GetRequiredService<IJobEvents>().Subscribe(_ => true, batch =>
        {
            lock (seen) seen.AddRange(batch);
            if (batch.FirstOrDefault(c => c.IsFinal) is { } done) finished.TrySetResult(done);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(10));

        var processor = provider.GetRequiredService<JobProcessor>();
        await processor.StartAsync(CancellationToken.None);
        try
        {
            var id = await provider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest { JobType = "sample", CreatedBy = "tophattwaffle" });
            var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(id, done.JobId);
            Assert.Equal(JobStatus.Succeeded, done.Status);
            Assert.Equal("Sample job", done.DisplayName);
            Assert.Equal("3 things done", done.Result);
            Assert.Equal("tophattwaffle", done.CreatedBy);
            lock (seen) Assert.Contains(seen, c => c.JobId == id && c.Status == JobStatus.Running);
            await using var ctx = db.CreateDbContext();
            Assert.Equal("3 things done", (await ctx.Jobs.SingleAsync()).Result);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }
}

public class OrderPollSummaryTests
{
    [Theory]
    [InlineData(0, 0, false, "no new or changed orders")]
    [InlineData(5, 2, false, "2 new, 3 updated")]
    [InlineData(1, 1, false, "1 new")]
    [InlineData(3, 0, false, "3 updated")]
    [InlineData(4547, 4547, true, "4547 order(s) in history")]
    public void Summarises_a_check_for_orders(int received, int created, bool backfill, string expected) =>
        Assert.Equal(expected, PollShopOrdersJob.Summary(new OrderPollResult(received, created, backfill)));
}
