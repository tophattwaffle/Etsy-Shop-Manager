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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

public class AdversarialSchedulerTimingTests
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

    // -------------------------------------------------------------------------
    // Test 1: Multiple consecutive runs over 72 hours under a 24-hour interval
    // Expected: enqueues exactly at 24h, 48h, 72h and never in between
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RecurringJobScheduler_ConsecutiveRuns_Over_72Hours_Enqueues_Exactly_At_24h_48h_72h()
    {
        using var db = new TestDb();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";

        var queue = new RecordingJobQueue();
        var def = CreateBackupJobDefinition();

        // Seed state as if the initial run just happened at t=0
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

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        var enqueuedHours = new List<int>();

        // Step through hour 1 to 72 (hourly checks)
        for (var hour = 1; hour <= 72; hour++)
        {
            time.Now = t0.AddHours(hour);
            await scheduler.ScheduleDueAsync(CancellationToken.None);

            if (queue.Requests.Count > 0)
            {
                enqueuedHours.Add(hour);
                Assert.Single(queue.Requests);
                queue.Requests.Clear(); // Job processed
            }
        }

        // Must enqueue ONLY at hours 24, 48, 72
        Assert.Equal(new[] { 24, 48, 72 }, enqueuedHours);
    }

    // -------------------------------------------------------------------------
    // Test 2: Dynamic interval change mid-cycle:
    // Last run at t=0. Interval changed from 24h to 6h at t=5.
    // Does it enqueue at t=6?
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DynamicIntervalChange_MidCycle_24h_To_6h_At_T5_Enqueues_At_T6_Via_Settings()
    {
        using var db = new TestDb();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";

        var queue = new RecordingJobQueue();
        var def = CreateBackupJobDefinition();

        // Seed state as if job enqueued at t=0
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

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // t=1..4: interval is 24h, nothing should enqueue
        for (var h = 1; h <= 4; h++)
        {
            time.Now = t0.AddHours(h);
            await scheduler.ScheduleDueAsync(CancellationToken.None);
            Assert.Empty(queue.Requests);
        }

        // At t=5: interval changed to 6h
        time.Now = t0.AddHours(5);
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";

        // At t=5: elapsed is 5h, interval is 6h -> NOT due yet
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=6: elapsed is 6h, interval is 6h -> DUE!
        time.Now = t0.AddHours(6);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    // -------------------------------------------------------------------------
    // Test 3: Changing interval from 6h to 24h at t=7:
    // Last run was at t=6.
    // Does it avoid enqueuing at t=8?
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DynamicIntervalChange_6h_To_24h_At_T7_Avoids_Enqueuing_At_T8_Via_Settings()
    {
        using var db = new TestDb();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";

        var queue = new RecordingJobQueue();
        var def = CreateBackupJobDefinition();

        // Seed state as if job enqueued at t=6 (last run)
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.AddHours(6).UtcDateTime,
                LastJobId = 2
            });
            await seedDb.SaveChangesAsync();
        }

        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t=7: interval changed from 6h to 24h in settings store
        time.Now = t0.AddHours(7);
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "24";

        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=8: elapsed since t=6 is 2h. Interval is 24h.
        // It MUST NOT enqueue at t=8!
        time.Now = t0.AddHours(8);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // In fact, verify it does not enqueue up to t=29
        for (var h = 9; h <= 29; h++)
        {
            time.Now = t0.AddHours(h);
            await scheduler.ScheduleDueAsync(CancellationToken.None);
            Assert.Empty(queue.Requests);
        }

        // At t=30: elapsed since t=6 is 24h -> Enqueues at t=30!
        time.Now = t0.AddHours(30);
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Single(queue.Requests);
    }

    // -------------------------------------------------------------------------
    // Test 4: Testing dynamic interval changes VIA BackupSettingsForm.SaveSettingsAsync
    // Demonstrating the bug: saving 24h at t=7 forces a backup to enqueue at t=8 (now + 1h).
    // -------------------------------------------------------------------------
    // Test 4: Testing dynamic interval changes VIA BackupSettingsForm.SaveSettingsAsync
    // Verifying: saving 24h at t=7 avoids enqueuing at t=8 (next run due at t=30).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DynamicIntervalChange_Via_BackupSettingsForm_At_T7_Check_T8_Behavior()
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = t0 };
        var settings = new InMemorySettings();
        settings.Values[SettingKeys.BackupsScheduleIntervalHours] = "6";
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

        // Seed state: last run occurred at t=6
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = t0.AddHours(6).UtcDateTime,
                LastJobId = 2
            });
            await seedDb.SaveChangesAsync();
        }

        var def = CreateBackupJobDefinition();
        var scheduler = new RecurringJobScheduler([def], settings, db, queue, time, NullLogger<RecurringJobScheduler>.Instance);

        // At t=7: User changes interval in BackupSettingsForm to 24 hours
        time.Now = t0.AddHours(7);
        SetField(form, "_scheduleHours", 24);
        SetField(form, "_savedScheduleHours", 6);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // At t=7: Scheduler checks -> not due
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        Assert.Empty(queue.Requests);

        // At t=8: Does it avoid enqueuing at t=8? (Only 2 hours since t=6 last run)
        time.Now = t0.AddHours(8);
        await scheduler.ScheduleDueAsync(CancellationToken.None);

        // Correct behavior: queue is empty at t=8 (due at t=30)
        Assert.Empty(queue.Requests);
    }

    [Fact]
    public Task DynamicIntervalChange_Via_BackupSettingsForm_At_T7_FailsToAvoid_Enqueuing_At_T8() =>
        DynamicIntervalChange_Via_BackupSettingsForm_At_T7_Check_T8_Behavior();

    // -------------------------------------------------------------------------
    // Test 5: Mathematical proof that BackupSettingsForm preserves the configured interval
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(168)]
    public async Task BackupSettingsForm_SaveSettingsAsync_Preserves_Configured_Interval_On_Existing_RecurringJobState(int configuredHours)
    {
        using var db = new TestDb();
        using var temp = new TempStorage();
        var saveTime = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = saveTime };
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

        // Existing state: job ran 1 hour before save (at 13:00)
        using (var seedDb = db.CreateDbContext())
        {
            seedDb.RecurringJobs.Add(new RecurringJobState
            {
                Name = "Scheduled backup",
                LastEnqueuedAtUtc = saveTime.AddHours(-1).UtcDateTime,
                LastJobId = 99
            });
            await seedDb.SaveChangesAsync();
        }

        // Save settings with configuredHours
        SetField(form, "_scheduleHours", configuredHours);
        SetField(form, "_savedScheduleHours", 1);
        await InvokeMethodAsync(form, "SaveSettingsAsync");

        // Inspect database state:
        using (var verifyDb = db.CreateDbContext())
        {
            var state = await verifyDb.RecurringJobs.FindAsync(["Scheduled backup"]);
            Assert.NotNull(state);

            // Because LastEnqueuedAtUtc is preserved from the previous run (saveTime - 1h),
            // next run time = LastEnqueuedAtUtc + configuredHours = saveTime - 1h + configuredHours.
            var nextDueTime = state.LastEnqueuedAtUtc!.Value.AddHours(configuredHours);
            var expectedDueTime = saveTime.AddHours(-1).UtcDateTime.AddHours(configuredHours);

            // The next run accurately reflects configuredHours!
            Assert.Equal(expectedDueTime, nextDueTime);
        }
    }
}
