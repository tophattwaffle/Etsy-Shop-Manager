using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class RecurringJobSchedulerTests
{
    [Fact]
    public async Task A_new_recurring_job_first_runs_after_its_initial_delay_with_the_interval_set_in_the_app()
    {
        using var db = new TestDb();
        var time = new ImmediateTimeProvider();
        var settings = new InMemorySettings();
        settings.Values["Poll.Seconds"] = "300"; // shorter than the built-in hour
        var queue = new RecordingJobQueue();
        var def = new RecurringJobDefinition("Poll", "poll", TimeSpan.FromHours(1))
        {
            IntervalSettingKey = "Poll.Seconds",
            InitialDelay = TimeSpan.FromSeconds(30),
        };
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        time.Now += TimeSpan.FromSeconds(31);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }
}

public class FileLoggerTests
{
    [Fact]
    public async Task Logging_carries_on_after_a_write_fails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "esm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileLoggerProvider(directory);
            var logger = provider.CreateLogger("Test");
            var file = Path.Combine(directory, $"esm-{DateTime.Now:yyyyMMdd}.log");

            // Another program holds the log file: the first line can't be written.
            using (new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                logger.LogInformation("while locked");
                await Task.Delay(500);
            }
            logger.LogInformation("after the lock");
            provider.Dispose(); // waits for the queued lines

            Assert.Contains("after the lock", await File.ReadAllTextAsync(file));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
