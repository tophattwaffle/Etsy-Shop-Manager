using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Backups;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM14IntervalStressTests
{
    // =========================================================================
    // 1. Extreme and Boundary Conditions for EffectiveInterval
    // =========================================================================

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-24")]
    [InlineData("-2147483648")] // int.MinValue
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("24 hours")]
    [InlineData("24h")]
    [InlineData("24.0")]
    [InlineData("2.5")]
    [InlineData("0.5")]
    [InlineData("1e3")]
    [InlineData("3000000000")] // Overflow int.MaxValue
    [InlineData("99999999999999999999")]
    public void EffectiveInterval_InvalidOrNonPositiveHours_FallsBackToDefaultInterval(string? rawValue)
    {
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        var effective = def.EffectiveInterval(rawValue);
        Assert.Equal(TimeSpan.FromHours(24), effective);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not_a_number")]
    [InlineData("60.5")]
    public void EffectiveInterval_InvalidOrNonPositiveSeconds_FallsBackToDefaultInterval(string? rawValue)
    {
        var def = new RecurringJobDefinition("Poll orders", "orders:poll", TimeSpan.FromMinutes(5))
        {
            IntervalSettingKey = SettingKeys.OrdersPollIntervalSeconds,
            IntervalUnit = IntervalUnit.Seconds,
            MinInterval = TimeSpan.FromSeconds(30),
        };

        var effective = def.EffectiveInterval(rawValue);
        Assert.Equal(TimeSpan.FromMinutes(5), effective);
    }

    [Fact]
    public void EffectiveInterval_ValidFormattedStrings_ParsedCorrectly()
    {
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        // Leading/trailing whitespace
        Assert.Equal(TimeSpan.FromHours(12), def.EffectiveInterval(" 12 "));

        // Leading zero
        Assert.Equal(TimeSpan.FromHours(8), def.EffectiveInterval("08"));

        // Leading plus
        Assert.Equal(TimeSpan.FromHours(6), def.EffectiveInterval("+6"));

        // Huge value (10,000 hours from prompt)
        Assert.Equal(TimeSpan.FromHours(10000), def.EffectiveInterval("10000"));

        // Max allowable in BackupSettingsForm (720 hours / 30 days)
        Assert.Equal(TimeSpan.FromHours(720), def.EffectiveInterval("720"));
    }

    [Fact]
    public void EffectiveInterval_HugeValues_And_OverflowBehavior_UnderIntMaxValue()
    {
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        // 10,000 hours (prompt specification) works cleanly
        Assert.Equal(TimeSpan.FromHours(10000), def.EffectiveInterval("10000"));

        // 1,000,000 hours (~114 years) works cleanly
        Assert.Equal(TimeSpan.FromHours(1000000), def.EffectiveInterval("1000000"));

        // int.MaxValue (2,147,483,647 hours) exceeds TimeSpan.MaxValue (~256,204,778 hours)
        // In .NET 10, TimeSpan.FromHours throws ArgumentOutOfRangeException:
        Assert.Throws<ArgumentOutOfRangeException>(() => def.EffectiveInterval(int.MaxValue.ToString()));
    }

    // =========================================================================
    // 2. MinInterval Clamping Edge Cases
    // =========================================================================

    [Fact]
    public void EffectiveInterval_Hours_SmallerThanMinInterval_ClampedToMinInterval()
    {
        var def = new RecurringJobDefinition("Custom backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(4), // MinInterval is 4 hours
        };

        // 1 hour is < 4 hours -> clamped to 4 hours
        Assert.Equal(TimeSpan.FromHours(4), def.EffectiveInterval("1"));

        // 2 hours is < 4 hours -> clamped to 4 hours
        Assert.Equal(TimeSpan.FromHours(4), def.EffectiveInterval("2"));

        // 3 hours is < 4 hours -> clamped to 4 hours
        Assert.Equal(TimeSpan.FromHours(4), def.EffectiveInterval("3"));

        // 4 hours == MinInterval -> 4 hours
        Assert.Equal(TimeSpan.FromHours(4), def.EffectiveInterval("4"));

        // 5 hours > MinInterval -> 5 hours
        Assert.Equal(TimeSpan.FromHours(5), def.EffectiveInterval("5"));
    }

    [Fact]
    public void EffectiveInterval_Seconds_SmallerThanMinInterval_ClampedToMinInterval()
    {
        var def = new RecurringJobDefinition("Fast poll", "fast:poll", TimeSpan.FromSeconds(60))
        {
            IntervalSettingKey = "FastPoll.IntervalSeconds",
            IntervalUnit = IntervalUnit.Seconds,
            MinInterval = TimeSpan.FromSeconds(45),
        };

        // 1 second -> clamped to 45s
        Assert.Equal(TimeSpan.FromSeconds(45), def.EffectiveInterval("1"));

        // 44 seconds -> clamped to 45s
        Assert.Equal(TimeSpan.FromSeconds(45), def.EffectiveInterval("44"));

        // 45 seconds -> 45s
        Assert.Equal(TimeSpan.FromSeconds(45), def.EffectiveInterval("45"));

        // 46 seconds -> 46s
        Assert.Equal(TimeSpan.FromSeconds(46), def.EffectiveInterval("46"));
    }

    [Fact]
    public void EffectiveInterval_FractionalMinInterval_HandlesCorrectly()
    {
        var def = new RecurringJobDefinition("Backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromMinutes(90), // 1.5 hours
        };

        // 1 hour (60 min) < 90 min -> clamped to 90 min
        Assert.Equal(TimeSpan.FromMinutes(90), def.EffectiveInterval("1"));

        // 2 hours (120 min) > 90 min -> 2 hours
        Assert.Equal(TimeSpan.FromHours(2), def.EffectiveInterval("2"));
    }

    // =========================================================================
    // 3. Unit Inference & Setting Key Combinations
    // =========================================================================

    [Theory]
    [InlineData("Jobs.IntervalHours", true)]
    [InlineData("jobs.intervalhours", true)]
    [InlineData("HOURS", true)]
    [InlineData("Hours", true)]
    [InlineData("Jobs.IntervalHoursX", false)]
    [InlineData("HoursAfterMidnight", false)]
    [InlineData("Jobs.IntervalSeconds", false)]
    [InlineData("", false)]
    public void EffectiveInterval_InfersHours_OnlyWhenKeyEndsWithHoursCaseInsensitive(string key, bool shouldInferHours)
    {
        var def = new RecurringJobDefinition("InferTest", "test", TimeSpan.FromDays(1))
        {
            IntervalSettingKey = key,
            IntervalUnit = IntervalUnit.Seconds, // Default
            MinInterval = TimeSpan.FromSeconds(1),
        };

        var result = def.EffectiveInterval("2");
        if (shouldInferHours)
        {
            Assert.Equal(TimeSpan.FromHours(2), result);
        }
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(2), result);
        }
    }

    [Fact]
    public void EffectiveInterval_ExplicitUnit_TakesPrecedenceOverKeyName()
    {
        // When IntervalUnit is explicitly set to Hours, key ending in Seconds does NOT downgrade to Seconds
        var def = new RecurringJobDefinition("HourDef", "test", TimeSpan.FromHours(10))
        {
            IntervalSettingKey = "SomeJob.IntervalSeconds",
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        Assert.Equal(TimeSpan.FromHours(3), def.EffectiveInterval("3"));
    }

    [Fact]
    public void EffectiveInterval_NullIntervalSettingKey_RespectsIntervalUnit()
    {
        var defHours = new RecurringJobDefinition("HoursNoKey", "test", TimeSpan.FromHours(10))
        {
            IntervalSettingKey = null,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        Assert.Equal(TimeSpan.FromHours(5), defHours.EffectiveInterval("5"));

        var defSeconds = new RecurringJobDefinition("SecondsNoKey", "test", TimeSpan.FromSeconds(300))
        {
            IntervalSettingKey = null,
            IntervalUnit = IntervalUnit.Seconds,
            MinInterval = TimeSpan.FromSeconds(30),
        };

        Assert.Equal(TimeSpan.FromSeconds(120), defSeconds.EffectiveInterval("120"));
    }

    // =========================================================================
    // 4. Scheduler Execution Adversarial Stress Tests
    // =========================================================================

    [Fact]
    public async Task RecurringJobScheduler_CorruptedOrZeroSetting_GracefullyUsesDefaultInterval()
    {
        using var db = new TestDb();
        var time = new ImmediateTimeProvider();
        var settings = new InMemorySettings();
        // Corrupted setting in database
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "GARBAGE_VALUE";

        var queue = new RecordingJobQueue();
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // Initial delay 1 hour
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 1 hour -> enqueues first run
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
        queue.Requests.Clear();

        // Advance 1 more hour -> because fallback is 24 hours, it must NOT fire at 1 hour!
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 23 more hours (24 total since last run) -> fires
        time.Now += TimeSpan.FromHours(23);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task RecurringJobScheduler_DynamicSettingChange_TakesEffectImmediatelyOnNextTick()
    {
        using var db = new TestDb();
        var time = new ImmediateTimeProvider();
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";

        var queue = new RecordingJobQueue();
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // T0: Initial registration (InitialDelay = 1h, so nothing enqueued)
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance past 1 hour initial delay -> enqueues initial run
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
        queue.Requests.Clear();

        // User changes setting in UI from 24 hours to 3 hours
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "3";

        // Advance 2 hours -> 2h < 3h -> not due yet
        time.Now += TimeSpan.FromHours(2);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 1 more hour (3 hours total since last run) -> fires under new 3-hour interval!
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task RecurringJobScheduler_HugeInterval_DoesNotPrematurelyFire()
    {
        using var db = new TestDb();
        var time = new ImmediateTimeProvider();
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "10000"; // 10,000 hours

        var queue = new RecordingJobQueue();
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // T0: Initial registration
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance past 1 hour initial delay -> fires
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
        queue.Requests.Clear();

        // Advance 5000 hours -> half way through, must NOT fire
        time.Now += TimeSpan.FromHours(5000);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance another 5000 hours (10,000 hours total) -> fires!
        time.Now += TimeSpan.FromHours(5000);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }
}
