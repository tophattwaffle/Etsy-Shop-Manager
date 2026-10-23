using System.Reflection;
using System.Security.Claims;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Components.Pages.Admin;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Backups;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical Challenger stress tests verifying that dynamic interval changes via BackupSettingsForm
/// do NOT fire prematurely at 1 hour after save when interval is 24 hours under diverse adversarial conditions.
/// </summary>
public class ChallengerDynamicIntervalChange24HoursTests
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

    private static RecurringJobDefinition CreateBackupJobDefinition() =>
        new("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            IntervalUnit = IntervalUnit.Hours,
            MinInterval = TimeSpan.FromHours(1),
        };

    private static BackupSettingsForm CreateForm(TestDb db, TempStorage temp, ISettingsStore settings, ImmediateTimeProvider time, RecordingJobQueue queue, NullAuditLog audit)
    {
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
        return form;
    }

    [Fact]
    public async Task DynamicChange_From_6h_To_24h_At_T1_DoesNotFire_At_T2_Fires_At_T24()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();

        // Seed state: last run occurred at t=0
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.UtcDateTime,
                LastJobId = 1
            });
            await seedDb.SaveChangesAsync();
        }

        var form = CreateForm(db, temp, settings, time, queue, audit);
        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t=1: user changes interval to 24 hours and saves
        time.Now = t0.AddHours(1);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 6);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // Verify hourly checks from hour 2 to 23 (including hour 2 = 1 hour after save!)
        for (var h = 2; h <= 23; h++)
        {
            time.Now = t0.AddHours(h);
            await scheduler.ScheduleDueAsync(CancellationToken.None);
            Assert.True(queue.Requests.Count == 0, $"Backup should not fire at hour {h} (t2 is 1h after save, interval is 24h).");
        }

        // At hour 24: full 24-hour interval elapsed since last run at t=0 -> fires!
        time.Now = t0.AddHours(24);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task DynamicChange_From_12h_To_24h_At_T11_5_DoesNotFire_At_T12_5_Fires_At_T24()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "12";
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();

        // Seed state: last run occurred at t=0
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.UtcDateTime,
                LastJobId = 1
            });
            await seedDb.SaveChangesAsync();
        }

        var form = CreateForm(db, temp, settings, time, queue, audit);
        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t=11.5: user changes interval to 24 hours right before 12h run
        time.Now = t0.AddHours(11.5);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 12);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // At t=12: old interval was 12h, but changed to 24h -> must NOT fire
        time.Now = t0.AddHours(12);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=12.5: exactly 1 hour after save at t=11.5 -> must NOT fire!
        time.Now = t0.AddHours(12.5);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=23: must NOT fire
        time.Now = t0.AddHours(23);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=24: fires!
        time.Now = t0.AddHours(24);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task Unchanged_24h_Interval_Saving_Retention_Only_DoesNotFire_At_1Hour_After_Save()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();

        // Seed state: last run occurred at t=0
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.UtcDateTime,
                LastJobId = 1
            });
            await seedDb.SaveChangesAsync();
        }

        var form = CreateForm(db, temp, settings, time, queue, audit);
        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t=10: user opens form, changes retention days to 60 (scheduleHours remains 24h), and clicks Save
        time.Now = t0.AddHours(10);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 24);
        SetField(form, "_retentionDays", 60);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // At t=11 (1 hour after save): must NOT fire
        time.Now = t0.AddHours(11);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Checks through t=23: must NOT fire
        for (var h = 12; h <= 23; h++)
        {
            time.Now = t0.AddHours(h);
            await scheduler.ScheduleDueAsync(CancellationToken.None);
            Assert.Empty(queue.Requests);
        }

        // At t=24: fires
        time.Now = t0.AddHours(24);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task Rapid_Successive_Saves_DoNotReset_Or_Prematurely_Fire()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();

        // Seed state: last run occurred at t=0
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.UtcDateTime,
                LastJobId = 1
            });
            await seedDb.SaveChangesAsync();
        }

        var form = CreateForm(db, temp, settings, time, queue, audit);
        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // Save 1: At t=2, user changes interval to 12h
        time.Now = t0.AddHours(2);
        SetField(form, "_scheduleHours", 12);
        SetField(form, "_savedScheduleHours", 6);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // Save 2: At t=3, user changes interval to 24h
        time.Now = t0.AddHours(3);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 12);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // Save 3: At t=3.5, user changes retention days (interval stays 24h)
        time.Now = t0.AddHours(3.5);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 24);
        SetField(form, "_retentionDays", 45);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // At t=4 (1 hour after Save 2): must NOT fire
        time.Now = t0.AddHours(4);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=4.5 (1 hour after Save 3): must NOT fire
        time.Now = t0.AddHours(4.5);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // Up to hour 23: must NOT fire
        for (var h = 5; h <= 23; h++)
        {
            time.Now = t0.AddHours(h);
            await scheduler.ScheduleDueAsync(CancellationToken.None);
            Assert.Empty(queue.Requests);
        }

        // At hour 24: fires!
        time.Now = t0.AddHours(24);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    [Fact]
    public async Task DynamicChange_24Hours_FutureTimestamp_ClampedToNow_DoesNotFire_At_1Hour()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";
        var audit = new NullAuditLog();
        var queue = new RecordingJobQueue();

        // Seed state: LastEnqueuedAtUtc is in the future (due to clock skew or manual edit) at t0 + 30 min
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.AddMinutes(30).UtcDateTime,
                LastJobId = 5
            });
            await seedDb.SaveChangesAsync();
        }

        var form = CreateForm(db, temp, settings, time, queue, audit);
        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t0: user changes interval to 24 hours. The code clamps LastEnqueuedAtUtc to now (t0)
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 6);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // Verify LastEnqueuedAtUtc was clamped to t0
        using (var verifyDb = db.CreateDbContext())
        {
            var state = await verifyDb.RecurringJobs.FindAsync(["Scheduled backup"]);
            Assert.NotNull(state);
            Assert.Equal(t0.UtcDateTime, state.LastEnqueuedAtUtc);
        }

        // At t0 + 1 hour (1 hour after save): must NOT fire
        time.Now = t0.AddHours(1);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t0 + 23 hours: must NOT fire
        time.Now = t0.AddHours(23);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t0 + 24 hours: fires!
        time.Now = t0.AddHours(24);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }
}
