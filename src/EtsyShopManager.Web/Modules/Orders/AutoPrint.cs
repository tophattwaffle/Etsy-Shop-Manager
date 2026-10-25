using System.Text.Json;
using System.Text.Json.Serialization;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>When packing slips go to the print station by themselves (Packing slips page).</summary>
public enum AutoPrintMode
{
    /// <summary>Nothing prints by itself.</summary>
    Off,

    /// <summary>Each new order prints as soon as it is found.</summary>
    EachOrder,

    /// <summary>All unprinted orders print together at a set time each day.</summary>
    Daily,

    /// <summary>All unprinted orders print at a set time, then new orders print as they arrive until the window ends.</summary>
    DailyThenWindow,
}

/// <summary>
/// The automatic printing settings. Times are the server's local time. Only orders first seen after automatic printing was
/// turned on (<paramref name="SinceUtc"/>) print by themselves, so turning it on doesn't print every open order at once.
/// </summary>
public sealed record AutoPrintSettings(AutoPrintMode Mode, TimeOnly At, TimeOnly Until, DateTime? SinceUtc)
{
    public static readonly AutoPrintSettings Default = new(AutoPrintMode.Off, new(8, 0), new(17, 0), null);

    /// <summary>Whether <paramref name="now"/> is in the window from <see cref="At"/> to <see cref="Until"/> (it may run past midnight).</summary>
    public bool InWindow(TimeOnly now) => At <= Until ? now >= At && now < Until : now >= At || now < Until;

    /// <summary>Whether an order found at local time <paramref name="now"/> prints straight away.</summary>
    public bool PrintsOnArrival(TimeOnly now) => Mode switch
    {
        AutoPrintMode.EachOrder => true,
        AutoPrintMode.DailyThenWindow => InWindow(now),
        _ => false,
    };

    /// <summary>
    /// Whether the daily batch is due at local time <paramref name="nowLocal"/>: the set time has passed today and today's
    /// batch hasn't run (<paramref name="lastBatch"/> is the local date of the last one). A batch missed while the app was
    /// stopped runs when it is back, the same day.
    /// </summary>
    public bool BatchDue(DateTime nowLocal, DateOnly? lastBatch) =>
        Mode is AutoPrintMode.Daily or AutoPrintMode.DailyThenWindow
        && TimeOnly.FromDateTime(nowLocal) >= At
        && lastBatch != DateOnly.FromDateTime(nowLocal);

    /// <summary>"Each new order prints as soon as it's found", in words for the Packing slips page.</summary>
    public string Describe() => Mode switch
    {
        AutoPrintMode.EachOrder => "Each new order prints as soon as it's found.",
        AutoPrintMode.Daily => $"Unprinted orders print together at {Time(At)} each day.",
        AutoPrintMode.DailyThenWindow =>
            $"Unprinted orders print together at {Time(At)}, then new orders print as they arrive until {Time(Until)}.",
        _ => "Off. Slips print only when you print them from Orders.",
    };

    private static string Time(TimeOnly t) => t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Automatic packing slip printing. The server can't reach a printer, so slips wait in a queue (<see cref="Order.SlipQueuedAtUtc"/>)
/// that the print station page empties: a browser left open on a PC by the printer, started with silent printing, asks
/// for queued slips every few seconds, prints them with the shop's layouts and reports back, which stamps
/// <see cref="Order.SlipPrintedAtUtc"/>. Slips queued while no station is open wait for one. Printing never writes to Etsy.
/// </summary>
public sealed class AutoPrintService(IDbContextFactory<AppDbContext> dbFactory, ISettingsStore settings, IAuditLog audit,
    TimeProvider time)
{
    public const string SettingsKey = "Orders.AutoPrint";

    /// <summary>The local date of the last daily batch (yyyy-MM-dd), so a restart doesn't print it twice.</summary>
    public const string LastBatchKey = "Orders.AutoPrint.LastBatch";

    /// <summary>How long slips handed to a station are kept from other stations; unconfirmed ones are offered again after.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>At most this many slips go to a station at once.</summary>
    public const int MaxBatch = 25;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Lock _gate = new();
    private readonly Dictionary<int, DateTime> _claims = [];

    /// <summary>When a print station last asked for slips (any station; not kept across restarts).</summary>
    public DateTime? StationSeenAtUtc { get; private set; }

    /// <summary>Raised when slips are queued or printed, so open pages refresh their printed marks and queue counts.</summary>
    public event Action? Changed;

    public async Task<AutoPrintSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingsKey, ct) is not { Length: > 0 } json) return AutoPrintSettings.Default;
        try { return JsonSerializer.Deserialize<AutoPrintSettings>(json, Json) ?? AutoPrintSettings.Default; }
        catch (JsonException) { return AutoPrintSettings.Default; }
    }

    /// <summary>Saves the settings. Turning automatic printing on starts counting new orders from now.</summary>
    public async Task<AutoPrintSettings> SaveSettingsAsync(AutoPrintSettings value, string? user, CancellationToken ct = default)
    {
        var old = await GetSettingsAsync(ct);
        var sinceUtc = value.Mode == AutoPrintMode.Off ? null
            : old.Mode == AutoPrintMode.Off || old.SinceUtc is null ? time.GetUtcNow().UtcDateTime : old.SinceUtc;
        var saved = value with { SinceUtc = sinceUtc };
        await settings.SetAsync(SettingsKey, JsonSerializer.Serialize(saved, Json), updatedBy: user, ct: ct);
        await audit.WriteAsync("orders.auto-print", null, null, saved.Describe(), user, ct);
        return saved;
    }

    private DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, time.LocalTimeZone);

    /// <summary>A new order was found: queue its slip when the settings print new orders now.</summary>
    public async Task OnOrderReceivedAsync(int orderId, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (!s.PrintsOnArrival(TimeOnly.FromDateTime(LocalNow))) return;
        await QueueWhereAsync(o => o.Id == orderId && o.SlipPrintedAtUtc == null, ct);
    }

    /// <summary>Queues every unprinted open order found since automatic printing was turned on, when the daily batch is due.</summary>
    /// <returns>How many slips were queued, or null when no batch was due.</returns>
    public async Task<int?> RunBatchIfDueAsync(CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        var now = LocalNow;
        DateOnly? last = DateOnly.TryParseExact(await settings.GetAsync(LastBatchKey, ct), "yyyy-MM-dd", out var d) ? d : null;
        if (!s.BatchDue(now, last)) return null;
        // Recorded first: a batch that fails part-way isn't repeated all day (the slips it queued stay queued).
        await settings.SetAsync(LastBatchKey, DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"), updatedBy: "auto-print", ct: ct);
        var since = s.SinceUtc ?? DateTime.MaxValue;
        return await QueueWhereAsync(o => o.SlipPrintedAtUtc == null && o.FirstSeenAtUtc >= since, ct);
    }

    /// <summary>Puts these orders' slips in the print station's queue (printed or not: this prints them again).</summary>
    public Task<int> QueueAsync(IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
    {
        var ids = orderIds.ToList();
        return QueueWhereAsync(o => ids.Contains(o.Id), ct);
    }

    // Open orders only (paid, not shipped or resolved); ones already queued keep their place.
    private async Task<int> QueueWhereAsync(System.Linq.Expressions.Expression<Func<Order, bool>> which, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var orders = (await db.Orders.Where(o => o.IsPaid && !o.IsShipped && o.SlipQueuedAtUtc == null).Where(which).ToListAsync(ct))
            .Where(o => o.IsOpen).ToList();
        if (orders.Count == 0) return 0;
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var o in orders) o.SlipQueuedAtUtc = now;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return orders.Count;
    }

    /// <summary>How many slips are waiting for the print station.</summary>
    public async Task<int> QueuedCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.CountAsync(o => o.SlipQueuedAtUtc != null, ct);
    }

    /// <summary>
    /// The next queued slips for a print station, oldest first, held for it for <see cref="Lease"/> so a second station
    /// doesn't print them too. Orders that shipped or closed while queued leave the queue unprinted.
    /// </summary>
    public async Task<List<int>> ClaimAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        StationSeenAtUtc = now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queued = await db.Orders.Where(o => o.SlipQueuedAtUtc != null).OrderBy(o => o.SlipQueuedAtUtc).ThenBy(o => o.Id).ToListAsync(ct);
        var closed = queued.Where(o => !o.IsOpen).ToList();
        if (closed.Count > 0)
        {
            foreach (var o in closed) o.SlipQueuedAtUtc = null;
            await db.SaveChangesAsync(ct);
            Changed?.Invoke();
        }
        lock (_gate)
        {
            foreach (var (id, at) in _claims.ToList())
                if (now - at >= Lease) _claims.Remove(id);
            var ids = queued.Where(o => o.IsOpen && !_claims.ContainsKey(o.Id)).Take(MaxBatch).Select(o => o.Id).ToList();
            foreach (var id in ids) _claims[id] = now;
            return ids;
        }
    }

    /// <summary>
    /// Stamps these orders' slips as printed now and takes them out of the queue (the print station after printing, or
    /// the Orders page once its print dialog closed).
    /// </summary>
    public async Task<int> MarkPrintedAsync(IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var orders = await db.Orders.Where(o => ids.Contains(o.Id)).ToListAsync(ct);
        foreach (var o in orders)
        {
            o.SlipPrintedAtUtc = now;
            o.SlipQueuedAtUtc = null;
        }
        await db.SaveChangesAsync(ct);
        lock (_gate) foreach (var id in ids) _claims.Remove(id);
        Changed?.Invoke();
        return orders.Count;
    }
}

/// <summary>Queues a new order's slip when automatic printing prints new orders straight away.</summary>
public sealed class AutoPrintOnOrderReceived(AutoPrintService autoPrint) : IDomainEventHandler<OrderReceived>
{
    public Task HandleAsync(OrderReceived e, CancellationToken ct) => autoPrint.OnOrderReceivedAsync(e.OrderId, ct);
}

/// <summary>Checks every half minute whether the daily batch of slips is due (see <see cref="AutoPrintSettings.BatchDue"/>).</summary>
public sealed class AutoPrintScheduler(AutoPrintService autoPrint, TimeProvider time, ILogger<AutoPrintScheduler> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await autoPrint.RunBatchIfDueAsync(ct) is { } queued)
                    logger.LogInformation("Daily packing slip batch: {Count} slip(s) queued for the print station", queued);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not queue the daily packing slip batch; trying again shortly");
            }
            await Task.Delay(Interval, time, ct);
        }
    }
}

/// <summary>The print station's report after printing: the orders whose slips printed.</summary>
public sealed record PrintedSlipsRequest(int[] Ids);
