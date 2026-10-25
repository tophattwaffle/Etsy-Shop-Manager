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

    private static (TestDb Db, AutoPrintService Service, Clock Clock, InMemorySettings Settings) Create()
    {
        var db = new TestDb();
        using (var ctx = db.CreateDbContext())
        {
            ctx.Shops.Add(new Shop { Id = 1, EtsyShopId = 42, Name = "SampleShop", ConnectedAtUtc = DateTime.UtcNow });
            ctx.SaveChanges();
        }
        var clock = new Clock();
        var settings = new InMemorySettings();
        return (db, new AutoPrintService(db, settings, new NullAuditLog(), clock), clock, settings);
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
    public async Task A_station_claims_slips_for_a_while_and_printing_stamps_them()
    {
        var (db, service, clock, _) = Create();
        using var _db = db;
        var a = AddOrder(db, 1001, clock.Now.UtcDateTime);
        var b = AddOrder(db, 1002, clock.Now.UtcDateTime);
        Assert.Equal(2, await service.QueueAsync([a, b]));
        Assert.Equal(0, await service.QueueAsync([a])); // already waiting

        Assert.Equal([a, b], await service.ClaimAsync());
        Assert.Equal(clock.Now.UtcDateTime, service.StationSeenAtUtc);
        Assert.Empty(await service.ClaimAsync()); // a second station doesn't print them too
        clock.Now = clock.Now + AutoPrintService.Lease;
        Assert.Equal([a, b], await service.ClaimAsync()); // never confirmed: offered again

        using (var ctx = db.CreateDbContext()) // b shipped before it printed: it leaves the queue
        {
            ctx.Orders.Single(o => o.Id == b).IsShipped = true;
            ctx.SaveChanges();
        }
        clock.Now = clock.Now + AutoPrintService.Lease;
        Assert.Equal([a], await service.ClaimAsync());
        Assert.Equal(1, await service.QueuedCountAsync());

        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal(1, await service.MarkPrintedAsync([a]));
        var printed = Orders(db).Single(o => o.Id == a);
        Assert.Equal((clock.Now.UtcDateTime, (DateTime?)null), (printed.SlipPrintedAtUtc, printed.SlipQueuedAtUtc));
        Assert.Null(Orders(db).Single(o => o.Id == b).SlipPrintedAtUtc);
        Assert.Equal(0, await service.QueuedCountAsync());
        Assert.Empty(await service.ClaimAsync());

        Assert.Equal(1, await service.QueueAsync([a])); // printed already: queuing it again prints it again
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
