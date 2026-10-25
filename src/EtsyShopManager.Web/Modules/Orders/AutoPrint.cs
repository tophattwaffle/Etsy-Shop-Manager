using System.Text.Json;
using System.Text.Json.Serialization;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>When packing slips print by themselves on the server's printer (Packing slips page).</summary>
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
/// <paramref name="Printer"/> is the server's printer that slips print on (null until one is chosen).
/// </summary>
public sealed record AutoPrintSettings(AutoPrintMode Mode, TimeOnly At, TimeOnly Until, DateTime? SinceUtc, string? Printer = null)
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
/// Automatic packing slip printing on the server's printer. Slips join a queue (<see cref="Order.SlipQueuedAtUtc"/>) by
/// the timing mode, and <see cref="PrintSlipsJob"/> prints the queue on the printer chosen on the Packing slips page
/// (<see cref="ISlipPrinter"/>), which stamps <see cref="Order.SlipPrintedAtUtc"/>. Slips that can't print (no printer
/// chosen, printer off) stay queued. Printing never writes to Etsy.
/// </summary>
public sealed class AutoPrintService(IDbContextFactory<AppDbContext> dbFactory, ISettingsStore settings, IAuditLog audit,
    IJobQueue jobs, TimeProvider time)
{
    public const string SettingsKey = "Orders.AutoPrint";

    /// <summary>The local date of the last daily batch (yyyy-MM-dd), so a restart doesn't print it twice.</summary>
    public const string LastBatchKey = "Orders.AutoPrint.LastBatch";

    /// <summary>At most this many slips print in one go (one PDF).</summary>
    public const int MaxBatch = 25;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Raised when slips are queued or printed, so open pages refresh their printed marks and queue counts.</summary>
    public event Action? Changed;

    public async Task<AutoPrintSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingsKey, ct) is not { Length: > 0 } json) return AutoPrintSettings.Default;
        try { return JsonSerializer.Deserialize<AutoPrintSettings>(json, Json) ?? AutoPrintSettings.Default; }
        catch (JsonException) { return AutoPrintSettings.Default; }
    }

    /// <summary>
    /// Saves the settings. Turning automatic printing on starts counting new orders from now. Slips left waiting (say, for
    /// a printer to be chosen) print once there is a printer.
    /// </summary>
    public async Task<AutoPrintSettings> SaveSettingsAsync(AutoPrintSettings value, string? user, CancellationToken ct = default)
    {
        var old = await GetSettingsAsync(ct);
        var sinceUtc = value.Mode == AutoPrintMode.Off ? null
            : old.Mode == AutoPrintMode.Off || old.SinceUtc is null ? time.GetUtcNow().UtcDateTime : old.SinceUtc;
        var saved = value with { SinceUtc = sinceUtc, Printer = string.IsNullOrWhiteSpace(value.Printer) ? null : value.Printer };
        await settings.SetAsync(SettingsKey, JsonSerializer.Serialize(saved, Json), updatedBy: user, ct: ct);
        await audit.WriteAsync("orders.auto-print", null, null, $"{saved.Describe()} Printer: {saved.Printer ?? "none"}.", user, ct);
        if (saved.Printer is not null && await QueuedCountAsync(ct) > 0) await EnqueuePrintAsync(user ?? "auto-print", ct);
        return saved;
    }

    private DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, time.LocalTimeZone);

    /// <summary>A new order was found: queue its slip when the settings print new orders now.</summary>
    public async Task OnOrderReceivedAsync(int orderId, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (!s.PrintsOnArrival(TimeOnly.FromDateTime(LocalNow))) return;
        await QueueWhereAsync(o => o.Id == orderId && o.SlipPrintedAtUtc == null, "new order", ct);
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
        return await QueueWhereAsync(o => o.SlipPrintedAtUtc == null && o.FirstSeenAtUtc >= since, "daily batch", ct);
    }

    /// <summary>Puts these orders' slips in the print queue (printed or not: this prints them again).</summary>
    public Task<int> QueueAsync(IReadOnlyCollection<int> orderIds, string? user, CancellationToken ct = default)
    {
        var ids = orderIds.ToList();
        return QueueWhereAsync(o => ids.Contains(o.Id), user ?? "auto-print", ct);
    }

    // Open orders only (paid, not shipped or resolved); ones already queued keep their place. Queuing starts a print job.
    private async Task<int> QueueWhereAsync(System.Linq.Expressions.Expression<Func<Order, bool>> which, string createdBy,
        CancellationToken ct)
    {
        int count;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var orders = (await db.Orders.Where(o => o.IsPaid && !o.IsShipped && o.SlipQueuedAtUtc == null).Where(which).ToListAsync(ct))
                .Where(o => o.IsOpen).ToList();
            if (orders.Count == 0) return 0;
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var o in orders) o.SlipQueuedAtUtc = now;
            await db.SaveChangesAsync(ct);
            count = orders.Count;
        }
        Changed?.Invoke();
        await EnqueuePrintAsync(createdBy, ct);
        return count;
    }

    /// <summary>Starts printing the queued slips (one print job at a time).</summary>
    public Task<long?> EnqueuePrintAsync(string createdBy, CancellationToken ct = default) =>
        jobs.EnqueueAsync(new JobRequest
        {
            JobType = PrintSlipsJob.Type,
            DedupeKey = PrintSlipsJob.Type,
            Priority = 20, // ahead of order polls and imports: someone may be waiting at the printer
            MaxAttempts = 3,
            CreatedBy = createdBy,
        }, ct);

    /// <summary>How many slips are waiting to print.</summary>
    public async Task<int> QueuedCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.CountAsync(o => o.SlipQueuedAtUtc != null, ct);
    }

    /// <summary>
    /// The next queued slips to print, oldest first (at most <see cref="MaxBatch"/>). Orders that shipped or closed while
    /// queued leave the queue unprinted.
    /// </summary>
    public async Task<List<int>> NextBatchAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queued = await db.Orders.Where(o => o.SlipQueuedAtUtc != null).OrderBy(o => o.SlipQueuedAtUtc).ThenBy(o => o.Id).ToListAsync(ct);
        var closed = queued.Where(o => !o.IsOpen).ToList();
        if (closed.Count > 0)
        {
            foreach (var o in closed) o.SlipQueuedAtUtc = null;
            await db.SaveChangesAsync(ct);
            Changed?.Invoke();
        }
        return [.. queued.Where(o => o.IsOpen).Take(MaxBatch).Select(o => o.Id)];
    }

    /// <summary>
    /// Stamps these orders' slips as printed now and takes them out of the queue (after the print job printed them, or
    /// once the Orders page's print dialog closed).
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
        Changed?.Invoke();
        return orders.Count;
    }
}

/// <summary>
/// Prints the queued packing slips on the chosen printer, a batch at a time (the same printout as Orders → Print packing
/// slips, loaded from this app). Fails without retrying when no printer is chosen or printing can't work on this server;
/// the slips stay queued and print once that is fixed (saving the settings starts this job again).
/// </summary>
public sealed class PrintSlipsJob(AutoPrintService autoPrint, ISlipPrinter printer, Microsoft.AspNetCore.Hosting.Server.IServer server)
    : IJobHandler
{
    public const string Type = "orders.print-slips";
    public string JobType => Type;
    public string DisplayName => "Print packing slips";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var settings = await autoPrint.GetSettingsAsync(ct);
        if (settings.Printer is null)
            throw new JobPermanentFailureException(
                $"{await autoPrint.QueuedCountAsync(ct)} slip(s) waiting: choose a printer on the Packing slips page.");
        if (printer.Problem is { } problem) throw new JobPermanentFailureException(problem);

        var printed = 0;
        while (await autoPrint.NextBatchAsync(ct) is { Count: > 0 } ids)
        {
            await printer.PrintAsync($"{WindowsSlipPrinter.LocalBaseUrl(server)}orders/print/slips?ids={string.Join(",", ids)}",
                settings.Printer, ct);
            printed += await autoPrint.MarkPrintedAsync(ids, ct);
        }
        context.Result = printed == 0 ? "nothing waiting to print" : $"{printed} slip(s) printed on {settings.Printer}";
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
                    logger.LogInformation("Daily packing slip batch: {Count} slip(s) queued to print", queued);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not queue the daily packing slip batch; trying again shortly");
            }
            await Task.Delay(Interval, time, ct);
        }
    }
}
