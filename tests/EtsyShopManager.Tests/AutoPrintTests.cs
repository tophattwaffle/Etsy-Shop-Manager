using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Tests;

/// <summary>Automatic packing slip printing: when slips join the print station's queue, and the station's claims (made-up orders).</summary>
public class AutoPrintTests
{
    /// <summary>A clock set by the test, in UTC so local times read the same on any machine.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 7, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly TimeOnly Eight = new(8, 0), Five = new(17, 0);

    private readonly RecordingJobQueue _jobs = new();

    private (TestDb Db, AutoPrintService Service, Clock Clock, InMemorySettings Settings) Create()
    {
        var db = new TestDb();
        using (var ctx = db.CreateDbContext())
        {
            ctx.Shops.Add(new Shop { Id = 1, EtsyShopId = 42, Name = "SampleShop", ConnectedAtUtc = DateTime.UtcNow });
            ctx.SaveChanges();
        }
        var clock = new Clock();
        var settings = new InMemorySettings();
        return (db, new AutoPrintService(db, settings, new NullAuditLog(), _jobs, clock), clock, settings);
    }

    private static int AddOrder(TestDb db, long receipt, DateTime firstSeen, bool shipped = false, DateTime? printed = null)
    {
        using var ctx = db.CreateDbContext();
        var order = new Order
        {
            ShopId = 1, EtsyReceiptId = receipt, BuyerName = "Sample Buyer", IsPaid = true, IsShipped = shipped, Status = "paid",
            FirstSeenAtUtc = firstSeen, LastSyncedAtUtc = firstSeen, SlipPrintedAtUtc = printed,
        };
        ctx.Orders.Add(order);
        ctx.SaveChanges();
        return order.Id;
    }

    private static List<Order> Orders(TestDb db)
    {
        using var ctx = db.CreateDbContext();
        return [.. ctx.Orders.AsNoTracking().OrderBy(o => o.Id)];
    }

    [Fact]
    public void Windows_can_run_past_midnight()
    {
        var day = new AutoPrintSettings(AutoPrintMode.DailyThenWindow, Eight, Five, null);
        Assert.True(day.InWindow(new TimeOnly(8, 0)));
        Assert.True(day.InWindow(new TimeOnly(16, 59)));
        Assert.False(day.InWindow(new TimeOnly(17, 0)));
        Assert.False(day.InWindow(new TimeOnly(7, 59)));

        var night = day with { At = new TimeOnly(22, 0), Until = new TimeOnly(2, 0) };
        Assert.True(night.InWindow(new TimeOnly(23, 30)));
        Assert.True(night.InWindow(new TimeOnly(1, 0)));
        Assert.False(night.InWindow(new TimeOnly(12, 0)));
    }

    [Fact]
    public void Each_mode_decides_what_prints_on_arrival_and_when_the_batch_is_due()
    {
        var noon = new TimeOnly(12, 0);
        Assert.False(AutoPrintSettings.Default.PrintsOnArrival(noon));
        Assert.True((AutoPrintSettings.Default with { Mode = AutoPrintMode.EachOrder }).PrintsOnArrival(noon));
        Assert.False((AutoPrintSettings.Default with { Mode = AutoPrintMode.Daily }).PrintsOnArrival(noon));
        var window = new AutoPrintSettings(AutoPrintMode.DailyThenWindow, Eight, Five, null);
        Assert.True(window.PrintsOnArrival(noon));
        Assert.False(window.PrintsOnArrival(new TimeOnly(18, 0)));

        var daily = new AutoPrintSettings(AutoPrintMode.Daily, Eight, Five, null);
        var today = new DateOnly(2026, 10, 15);
        Assert.False(daily.BatchDue(new DateTime(2026, 10, 15, 7, 59, 0), null)); // not time yet
        Assert.True(daily.BatchDue(new DateTime(2026, 10, 15, 8, 0, 0), null));
        Assert.True(daily.BatchDue(new DateTime(2026, 10, 15, 13, 0, 0), today.AddDays(-1))); // missed while stopped: runs late
        Assert.False(daily.BatchDue(new DateTime(2026, 10, 15, 13, 0, 0), today)); // once a day
        Assert.True(window.BatchDue(new DateTime(2026, 10, 15, 8, 0, 0), null));
        Assert.False((daily with { Mode = AutoPrintMode.EachOrder }).BatchDue(new DateTime(2026, 10, 15, 8, 0, 0), null));
    }

    [Fact]
    public async Task Turning_it_on_counts_new_orders_from_then_and_keeps_that_start()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        Assert.Equal(AutoPrintSettings.Default, await service.GetSettingsAsync());

        var on = await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.Daily, Eight, Five, null), "tophattwaffle");
        Assert.Equal(clock.Now.UtcDateTime, on.SinceUtc);
        Assert.Equal(on, await service.GetSettingsAsync());

        clock.Now = clock.Now.AddHours(3);
        var changed = await service.SaveSettingsAsync(on with { Mode = AutoPrintMode.EachOrder }, "tophattwaffle");
        Assert.Equal(on.SinceUtc, changed.SinceUtc); // still on: the start stays

        Assert.Null((await service.SaveSettingsAsync(changed with { Mode = AutoPrintMode.Off }, "tophattwaffle")).SinceUtc);
    }

    [Fact]
    public async Task New_orders_print_straight_away_only_when_the_settings_say_so()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        var first = AddOrder(db, 1001, clock.Now.UtcDateTime);

        await service.OnOrderReceivedAsync(first); // off
        Assert.Null(Orders(db)[0].SlipQueuedAtUtc);

        await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.DailyThenWindow, Eight, Five, null), "tophattwaffle");
        await service.OnOrderReceivedAsync(first); // 07:00, before the window
        Assert.Null(Orders(db)[0].SlipQueuedAtUtc);

        clock.Now = clock.Now.AddHours(2); // 09:00, in the window
        var second = AddOrder(db, 1002, clock.Now.UtcDateTime);
        await service.OnOrderReceivedAsync(second);
        Assert.Equal([null, clock.Now.UtcDateTime], Orders(db).Select(o => o.SlipQueuedAtUtc));

        await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.EachOrder, Eight, Five, null), "tophattwaffle");
        var printed = AddOrder(db, 1003, clock.Now.UtcDateTime, printed: clock.Now.UtcDateTime);
        await service.OnOrderReceivedAsync(printed); // its slip already printed
        Assert.Null(Orders(db)[2].SlipQueuedAtUtc);
    }

    [Fact]
    public async Task The_daily_batch_queues_unprinted_open_orders_since_it_was_turned_on_once_a_day()
    {
        var (db, service, clock, settings) = Create();
        using var _db = db;
        var before = AddOrder(db, 1000, clock.Now.UtcDateTime.AddDays(-1)); // already open when it was turned on
        await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.Daily, Eight, Five, null), "tophattwaffle");
        clock.Now = clock.Now.AddMinutes(30);
        var waiting = AddOrder(db, 1001, clock.Now.UtcDateTime);
        AddOrder(db, 1002, clock.Now.UtcDateTime, shipped: true);
        AddOrder(db, 1003, clock.Now.UtcDateTime, printed: clock.Now.UtcDateTime);

        Assert.Null(await service.RunBatchIfDueAsync()); // 07:30: not yet
        clock.Now = clock.Now.AddMinutes(31); // 08:01
        Assert.Equal(1, await service.RunBatchIfDueAsync());
        Assert.Equal([waiting], Orders(db).Where(o => o.SlipQueuedAtUtc != null).Select(o => o.Id));
        Assert.Equal("2026-10-15", settings.Values[AutoPrintService.LastBatchKey]);
        Assert.Null(await service.RunBatchIfDueAsync()); // done for today
        Assert.Null(Orders(db).Single(o => o.Id == before).SlipQueuedAtUtc);
    }

    [Fact]
    public async Task Queued_slips_start_a_print_job_and_closed_orders_leave_the_queue()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        var a = AddOrder(db, 1001, clock.Now.UtcDateTime);
        var b = AddOrder(db, 1002, clock.Now.UtcDateTime);
        Assert.Equal(2, await service.QueueAsync([a, b], "tophattwaffle"));
        Assert.Equal(0, await service.QueueAsync([a], "tophattwaffle")); // already waiting
        var job = Assert.Single(_jobs.Requests);
        Assert.Equal((PrintSlipsJob.Type, PrintSlipsJob.Type), (job.JobType, job.DedupeKey));

        using (var ctx = db.CreateDbContext()) // b shipped before it printed: it leaves the queue
        {
            ctx.Orders.Single(o => o.Id == b).IsShipped = true;
            ctx.SaveChanges();
        }
        Assert.Equal([a], await service.NextBatchAsync());
        Assert.Equal(1, await service.QueuedCountAsync());

        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal(1, await service.MarkPrintedAsync([a]));
        var printed = Orders(db).Single(o => o.Id == a);
        Assert.Equal((clock.Now.UtcDateTime, (DateTime?)null), (printed.SlipPrintedAtUtc, printed.SlipQueuedAtUtc));
        Assert.Null(Orders(db).Single(o => o.Id == b).SlipPrintedAtUtc);
        Assert.Empty(await service.NextBatchAsync());

        Assert.Equal(1, await service.QueueAsync([a], "tophattwaffle")); // printed already: queuing it again prints it again
    }

    /// <summary>Records what it was asked to print; fails when told to.</summary>
    private sealed class FakePrinter : ISlipPrinter
    {
        public List<(string Url, string Printer)> Printed { get; } = [];
        public string? Problem { get; set; }
        public bool Fail { get; set; }
        public IReadOnlyList<string> ListPrinters() => ["Slip Printer"];

        public Task PrintAsync(string url, string printer, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("SumatraPDF failed (exit code 1).");
            Printed.Add((url, printer));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServer : Microsoft.AspNetCore.Hosting.Server.IServer
    {
        public FakeServer(string address)
        {
            var addresses = new Microsoft.AspNetCore.Hosting.Server.Features.ServerAddressesFeature();
            addresses.Addresses.Add(address);
            Features.Set<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>(addresses);
        }

        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static EtsyShopManager.Core.Jobs.JobExecutionContext JobContext() =>
        new() { JobId = 1, JobType = PrintSlipsJob.Type, Attempt = 1 };

    [Fact]
    public async Task The_print_job_prints_the_queue_on_the_chosen_printer_from_the_apps_own_address()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        var printer = new FakePrinter();
        var job = new PrintSlipsJob(service, printer, new FakeServer("http://0.0.0.0:5080"));
        var a = AddOrder(db, 1001, clock.Now.UtcDateTime);
        var b = AddOrder(db, 1002, clock.Now.UtcDateTime);
        await service.QueueAsync([a, b], "tophattwaffle");

        var noPrinter = await Assert.ThrowsAsync<EtsyShopManager.Core.Jobs.JobPermanentFailureException>(() => job.HandleAsync(JobContext(), default));
        Assert.Equal("2 slip(s) waiting: choose a printer on the Packing slips page.", noPrinter.Message);
        Assert.Equal(2, await service.QueuedCountAsync()); // still waiting

        await service.SaveSettingsAsync(AutoPrintSettings.Default with { Printer = "Slip Printer" }, "tophattwaffle");
        printer.Problem = "SumatraPDF isn't installed on the server.";
        await Assert.ThrowsAsync<EtsyShopManager.Core.Jobs.JobPermanentFailureException>(() => job.HandleAsync(JobContext(), default));
        printer.Problem = null;

        printer.Fail = true; // printer trouble: the job retries, the slips stay queued
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.HandleAsync(JobContext(), default));
        Assert.Equal(2, await service.QueuedCountAsync());
        printer.Fail = false;

        var context = JobContext();
        await job.HandleAsync(context, default);
        Assert.Equal(($"http://127.0.0.1:5080/orders/print/slips?ids={a},{b}", "Slip Printer"), Assert.Single(printer.Printed));
        Assert.Equal("2 slip(s) printed on Slip Printer", context.Result);
        Assert.All(Orders(db), o => Assert.NotNull(o.SlipPrintedAtUtc));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080", "http://127.0.0.1:5080/")]
    [InlineData("http://+:5085", "http://127.0.0.1:5085/")]
    [InlineData("http://127.0.0.1:5083", "http://127.0.0.1:5083/")]
    [InlineData("http://[::]:5101", "http://127.0.0.1:5101/")]
    public void Edge_loads_the_printout_from_the_apps_local_address(string listening, string expected) =>
        Assert.Equal(expected, WindowsSlipPrinter.LocalBaseUrl(new FakeServer(listening)));

    [Fact]
    public void The_hosts_printers_can_be_listed()
    {
        var printers = new WindowsSlipPrinter(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new EtsyShopManager.Core.Configuration.StorageOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsSlipPrinter>.Instance)
            .ListPrinters();
        Assert.Equal(printers.Order(StringComparer.OrdinalIgnoreCase), printers); // sorted, and EnumPrinters didn't fail
        Assert.All(printers, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public async Task Choosing_a_printer_prints_slips_left_waiting()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        await service.QueueAsync([AddOrder(db, 1001, clock.Now.UtcDateTime)], "tophattwaffle");
        _jobs.Requests.Clear(); // that job failed: no printer yet
        await service.SaveSettingsAsync(AutoPrintSettings.Default with { Printer = "Slip Printer" }, "tophattwaffle");
        Assert.Equal(PrintSlipsJob.Type, Assert.Single(_jobs.Requests).JobType);
    }

    [Fact]
    public async Task A_slip_printed_by_hand_is_never_printed_again_automatically()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.DailyThenWindow, Eight, Five, null, "Slip Printer"), "tophattwaffle");
        clock.Now = clock.Now.AddMinutes(10); // 07:10, before the daily batch
        var byHand = AddOrder(db, 1001, clock.Now.UtcDateTime);
        var queuedThenByHand = AddOrder(db, 1002, clock.Now.UtcDateTime);
        await service.QueueAsync([queuedThenByHand], "tophattwaffle");

        await service.MarkPrintedAsync([byHand, queuedThenByHand]); // Orders → Print packing slips
        Assert.Empty(await service.NextBatchAsync()); // the waiting one left the queue

        clock.Now = clock.Now.AddHours(1); // 08:10: the daily batch
        Assert.Equal(0, await service.RunBatchIfDueAsync());
        await service.OnOrderReceivedAsync(byHand); // in the window
        Assert.All(Orders(db), o => Assert.Null(o.SlipQueuedAtUtc));
    }

    [Fact]
    public void Print_packing_slips_with_nothing_selected_prints_the_unprinted_open_orders()
    {
        var day = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        Order O(int id, DateTime? shipBy, DateTime? printed = null, bool shipped = false) => new()
        {
            Id = id, EtsyReceiptId = 1000 + id, IsPaid = true, IsShipped = shipped, Status = "paid", ShipByUtc = shipBy, SlipPrintedAtUtc = printed,
        };
        var orders = new[] { O(1, day.AddDays(2)), O(2, day, printed: day), O(3, day), O(4, null), O(5, day, shipped: true) };
        Assert.Equal([3, 1, 4], OrderPrintService.UnprintedIds(orders)); // soonest ship-by first; printed and shipped left out
    }

    [Fact]
    public async Task A_new_order_is_queued_through_the_order_received_event()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        await service.SaveSettingsAsync(new AutoPrintSettings(AutoPrintMode.EachOrder, Eight, Five, null), "tophattwaffle");
        var id = AddOrder(db, 1001, clock.Now.UtcDateTime);
        await new AutoPrintOnOrderReceived(service).HandleAsync(new EtsyShopManager.Core.Events.OrderReceived(1, id, 1001), default);
        Assert.NotNull(Orders(db)[0].SlipQueuedAtUtc);
    }
}
