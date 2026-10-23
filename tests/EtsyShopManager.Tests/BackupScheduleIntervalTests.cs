using System.Reflection;
using System.Security.Claims;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Components.Pages.Admin;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Backups;
using EtsyShopManager.Web.Infrastructure.Jobs;
using EtsyShopManager.Web.Modules;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

public class BackupScheduleIntervalTests
{
    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field {fieldName} not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static void SetProperty(object target, string propName, object? value)
    {
        var prop = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property {propName} not found on {target.GetType().Name}");
        prop.SetValue(target, value);
    }

    private static async Task InvokeMethodAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Method {methodName} not found on {target.GetType().Name}");
        var task = (Task)method.Invoke(target, null)!;
        await task;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
    }

    private static (ISnackbar Snackbar, IDialogService Dialogs) CreateMudServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddMudServices();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ISnackbar>(), sp.GetRequiredService<IDialogService>());
    }

    private static ClaimsPrincipal CreateAdminUser() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, Roles.Admin)
        }, "auth"));

    // -------------------------------------------------------------------------
    // 1. RecurringJobDefinition & EffectiveInterval Unit Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void RecurringJobDefinition_EffectiveInterval_With_Hours_Unit_Parses_Correctly()
    {
        var def = new RecurringJobDefinition("Scheduled backup", "backup:create", TimeSpan.FromHours(24))
        {
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        // Configured 24 hours -> 24 hours duration (NOT clamped to 1 hour)
        Assert.Equal(TimeSpan.FromHours(24), def.EffectiveInterval("24"));

        // Configured 6 hours -> 6 hours duration
        Assert.Equal(TimeSpan.FromHours(6), def.EffectiveInterval("6"));

        // Configured 1 hour -> 1 hour duration
        Assert.Equal(TimeSpan.FromHours(1), def.EffectiveInterval("1"));

        // Configured below MinInterval (e.g., 0) or invalid -> fallback to Interval (24 hours)
        Assert.Equal(TimeSpan.FromHours(24), def.EffectiveInterval("0"));
        Assert.Equal(TimeSpan.FromHours(24), def.EffectiveInterval("-1"));
        Assert.Equal(TimeSpan.FromHours(24), def.EffectiveInterval("invalid"));
        Assert.Equal(TimeSpan.FromHours(24), def.EffectiveInterval(null));
    }

    [Fact]
    public void RecurringJobDefinition_EffectiveInterval_With_Seconds_Unit_Parses_Correctly()
    {
        var def = new RecurringJobDefinition("Poll orders", "orders:poll", TimeSpan.FromMinutes(5))
        {
            IntervalSettingKey = SettingKeys.OrdersPollIntervalSeconds,
            IntervalUnit = IntervalUnit.Seconds,
            MinInterval = TimeSpan.FromSeconds(30),
        };

        // 300 seconds
        Assert.Equal(TimeSpan.FromSeconds(300), def.EffectiveInterval("300"));

        // 10 seconds (below 30s min) -> clamped to 30 seconds
        Assert.Equal(TimeSpan.FromSeconds(30), def.EffectiveInterval("10"));

        // Invalid -> fallback to Interval (5 minutes)
        Assert.Equal(TimeSpan.FromMinutes(5), def.EffectiveInterval(null));
        Assert.Equal(TimeSpan.FromMinutes(5), def.EffectiveInterval("abc"));
    }

    [Fact]
    public void RecurringJobDefinition_EffectiveInterval_Infers_Hours_If_SettingKey_EndsWith_Hours()
    {
        // IntervalUnit defaults to Seconds, but setting key ends with "Hours"
        var def = new RecurringJobDefinition("Test Hours", "test", TimeSpan.FromHours(12))
        {
            IntervalSettingKey = "MyModule.IntervalHours",
            MinInterval = TimeSpan.FromHours(1),
        };

        Assert.Equal(IntervalUnit.Seconds, def.IntervalUnit);
        // Automatically treats as hours because key ends with "Hours"
        Assert.Equal(TimeSpan.FromHours(12), def.EffectiveInterval("12"));
        Assert.Equal(TimeSpan.FromHours(48), def.EffectiveInterval("48"));
    }

    // -------------------------------------------------------------------------
    // 2. AdminModule Registration & GetIntervalAsync Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void AdminModule_Registers_ScheduledBackup_With_IntervalUnit_Hours()
    {
        var services = new ServiceCollection();
        var adminModule = new AdminModule();
        adminModule.ConfigureServices(services, new Microsoft.Extensions.Configuration.ConfigurationManager());

        var provider = services.BuildServiceProvider();
        var definitions = provider.GetServices<RecurringJobDefinition>().ToList();
        var backupDef = Assert.Single(definitions, d => d.Name == "Scheduled backup");

        Assert.Equal(IntervalUnit.Hours, backupDef.IntervalUnit);
        Assert.Equal(SettingKeys.BackupsScheduleIntervalHours, backupDef.IntervalSettingKey);
        Assert.Equal(TimeSpan.FromHours(24), backupDef.Interval);
        Assert.Equal(TimeSpan.FromHours(1), backupDef.MinInterval);
        Assert.Equal(TimeSpan.FromHours(1), backupDef.InitialDelay);
    }

    [Fact]
    public async Task RecurringJobScheduler_GetIntervalAsync_For_ScheduledBackup_Returns_Configured_Hours()
    {
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

        var settings = new InMemorySettings();

        // 1. Setting = "24" -> 24 hours
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";
        var interval24 = await RecurringJobScheduler.GetIntervalAsync(def, settings);
        Assert.Equal(TimeSpan.FromHours(24), interval24);

        // 2. Setting = "6" -> 6 hours
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";
        var interval6 = await RecurringJobScheduler.GetIntervalAsync(def, settings);
        Assert.Equal(TimeSpan.FromHours(6), interval6);

        // 3. Setting null/missing -> default 24 hours
        settings.Values.Remove(SettingKeys.BackupsScheduleIntervalHours);
        var intervalDefault = await RecurringJobScheduler.GetIntervalAsync(def, settings);
        Assert.Equal(TimeSpan.FromHours(24), intervalDefault);
    }

    // -------------------------------------------------------------------------
    // 3. RecurringJobScheduler.ScheduleDueAsync 24-Hour Interval Execution Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RecurringJobScheduler_ScheduleDueAsync_Under_24Hour_Interval_Does_Not_Enqueue_At_1Hour_And_Enqueues_At_24Hours()
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

        // 1. First run on clean install: InitialDelay is 1 hour, so nothing enqueued at T0
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 30 minutes -> still within initial delay, nothing enqueued
        time.Now += TimeSpan.FromMinutes(30);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance past 1 hour (InitialDelay elapsed) -> First backup job enqueued!
        time.Now += TimeSpan.FromMinutes(31);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
        Assert.Equal(CreateBackupJob.Type, queue.Requests[0].JobType);
        queue.Requests.Clear(); // Simulates the first job being processed and cleared from queue

        // 2. Critical verification of bug fix:
        // Advance time by 1 more hour (2 hours total elapsed).
        // Under the old bug, interval was clamped to 1 hour, so it would fire again!
        // With the fix, 24-hour interval is active, so it must NOT enqueue.
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests); // Must not enqueue!

        // Advance by another 10 hours (11 hours since last enqueue) -> still nothing
        time.Now += TimeSpan.FromHours(10);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance by 12 more hours (23 hours since last enqueue) -> still nothing
        time.Now += TimeSpan.FromHours(12);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 1 more hour and 1 minute (24+ hours since last enqueue) -> Second backup job enqueued!
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    // -------------------------------------------------------------------------
    // 4. BackupSettingsForm Saving Updates RecurringJobState in Database Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BackupSettingsForm_SaveSettingsAsync_Creates_RecurringJobState_When_Not_Present()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var time = new ImmediateTimeProvider { Now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero) };
        var settings = new InMemorySettings();
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();
        var (snackbar, dialogs) = CreateMudServices();

        var backupService = new BackupService(temp.Options, db, settings, audit, time, NullLogger<BackupService>.Instance);

        var form = new BackupSettingsForm();
        SetProperty(form, "Backups", backupService);
        SetProperty(form, "Settings", settings);
        SetProperty(form, "Storage", temp.Options);
        SetProperty(form, "Jobs", queue);
        SetProperty(form, "Audit", audit);
        SetProperty(form, "Snackbar", snackbar);
        SetProperty(form, "Dialogs", dialogs);
        SetProperty(form, "DbFactory", db);
        SetProperty(form, "Time", time);
        SetProperty(form, "AuthState", Task.FromResult(new AuthenticationState(CreateAdminUser())));

        // Set interval to 24 hours
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 12); // changed

        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // 1. Verify setting is stored
        Assert.Equal("24", settings.Values[SettingKeys.BackupsScheduleIntervalHours]);

        // 2. Verify RecurringJobState in DB was created
        using (var verifyDb = db.CreateDbContext())
        {
            var state = await verifyDb.RecurringJobs.FindAsync(["Scheduled backup"]);
            Assert.NotNull(state);
            Assert.Equal("Scheduled backup", state.Name);

            // Fresh install: LastEnqueuedAtUtc = now (12:00) - 24h + 1h = 12:00 - 23h = yesterday 13:00 (1 hour initial delay)
            var expectedLastEnqueued = time.Now.UtcDateTime - TimeSpan.FromHours(24) + TimeSpan.FromHours(1);
            Assert.Equal(expectedLastEnqueued, state.LastEnqueuedAtUtc);
        }

        // 3. Verify scheduler with this state does not immediately fire at T0
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Fires after 1 hour (InitialDelay on clean install)
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
        queue.Requests.Clear();

        // 4. Verify the next scheduled run reflects the configured 24 hours (does NOT fire after 1 hour)
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests); // Must not fire after 1 hour

        // Advance 22 more hours (23 hours since last enqueue) -> still empty
        time.Now += TimeSpan.FromHours(22);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advance 1 more hour and 1 second (24+ hours since last enqueue) -> Second backup enqueued!
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task BackupSettingsForm_SaveSettingsAsync_Updates_Existing_RecurringJobState_In_Database()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var time = new ImmediateTimeProvider { Now = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero) };
        var settings = new InMemorySettings();
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();
        var (snackbar, dialogs) = CreateMudServices();

        // Seed DB with existing RecurringJobState from a previous run 2 hours ago (13:00 UTC)
        var seedLastEnqueued = time.Now.UtcDateTime.AddHours(-2);
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = seedLastEnqueued,
                LastJobId = 42
            });
            await seedDb.SaveChangesAsync();
        }

        var backupService = new BackupService(temp.Options, db, settings, audit, time, NullLogger<BackupService>.Instance);

        var form = new BackupSettingsForm();
        SetProperty(form, "Backups", backupService);
        SetProperty(form, "Settings", settings);
        SetProperty(form, "Storage", temp.Options);
        SetProperty(form, "Jobs", queue);
        SetProperty(form, "Audit", audit);
        SetProperty(form, "Snackbar", snackbar);
        SetProperty(form, "Dialogs", dialogs);
        SetProperty(form, "DbFactory", db);
        SetProperty(form, "Time", time);
        SetProperty(form, "AuthState", Task.FromResult(new AuthenticationState(CreateAdminUser())));

        // Change interval to 6 hours
        SetField(form, "_scheduleHours", 6);
        SetField(form, "_savedScheduleHours", 24);

        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // 1. Setting saved
        Assert.Equal("6", settings.Values[SettingKeys.BackupsScheduleIntervalHours]);

        // 2. State in DB preserves the actual last run time (NOT overwritten with now - interval + 1h)
        using (var verifyDb = db.CreateDbContext())
        {
            var state = await verifyDb.RecurringJobs.FindAsync(["Scheduled backup"]);
            Assert.NotNull(state);

            Assert.Equal(seedLastEnqueued, state.LastEnqueuedAtUtc);
            Assert.Equal(42, state.LastJobId); // Preserves existing job ID
        }

        // 3. Verify scheduler with this state does not immediately fire at T0
        var def = new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // 4. Advances 1 hour (3 hours elapsed since last run) -> Does NOT fire prematurely!
        time.Now += TimeSpan.FromHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advances another 2 hours (5 hours elapsed since last run) -> Still does not fire
        time.Now += TimeSpan.FromHours(2);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Advances 1 more hour and 1 second (6+ hours elapsed since last run) -> Next scheduled run fires!
        time.Now += TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1));
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);

        // 5. Verify that saving settings without changing the interval leaves LastEnqueuedAtUtc untouched
        queue.Requests.Clear();
        SetField(form, "_scheduleHours", 6);
        SetField(form, "_savedScheduleHours", 6);
        SetField(form, "_retentionDays", 14); // only retention changed
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        using (var verifyDb = db.CreateDbContext())
        {
            var state = await verifyDb.RecurringJobs.FindAsync(["Scheduled backup"]);
            Assert.NotNull(state);
            // LastEnqueuedAtUtc was set by scheduler to time.Now when enqueued, and was not reset by form save
            Assert.Equal(time.Now.UtcDateTime, state.LastEnqueuedAtUtc);
        }
    }
}
