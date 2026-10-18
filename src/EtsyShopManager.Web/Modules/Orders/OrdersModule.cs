using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Phase 2: order polling, the Orders page, API keys and the Home Assistant API.</summary>
public sealed class OrdersModule : IModule
{
    /// <summary>The old etsyapp integration polled every 300 s; the same default, changeable in Admin → Settings.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(300);

    public string Name => "Orders";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<OrderPoller>();
        services.AddSingleton<OrderQueries>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<OrderPollQueue>();
        services.AddSingleton<OrderColorService>();
        services.AddSingleton<OrderPrintService>();
        services.AddSingleton<OrderOverrideService>();
        services.AddSingleton<SlipLayoutService>();
        services.AddSingleton<OrderMadeService>();

        services.AddJobHandler<PollShopOrdersJob>();
        services.AddJobHandler<PollAllOrdersJob>();
        services.AddJobHandler<GiftBuyerNamesJob>();
        services.AddHostedService<GiftBuyerNamesStartup>();
        services.AddScoped<IDomainEventHandler<ShopConnected>, PollOrdersOnShopConnected>();
        services.AddScoped<IDomainEventHandler<ShopConnected>, GiftBuyerNamesOnShopConnected>();

        services.AddRecurringJob(new RecurringJobDefinition("Poll orders (all shops)", PollAllOrdersJob.Type, DefaultPollInterval)
        {
            IntervalSettingKey = SettingKeys.OrdersPollIntervalSeconds,
            MinInterval = TimeSpan.FromSeconds(60),
            InitialDelay = TimeSpan.FromSeconds(30),
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var ha = endpoints.MapGroup("/api/ha/v1").AllowAnonymous().AddEndpointFilter<ApiKeyEndpointFilter>();

        ha.MapGet("/shops", async (OrderQueries q, CancellationToken ct) => Results.Json(await q.HaShopsAsync(ct)));
        ha.MapGet("/open-orders", async (OrderQueries q, OrderColorService colors,
            EtsyShopManager.Web.Modules.Shops.ShopService shops, EtsyShopManager.Web.Modules.Shops.ShopAccentService accents,
            CancellationToken ct) =>
        {
            var payload = await q.HaOpenOrdersAllAsync(ct);
            // The order color list and the shops' accent colors ride along, so the HA card colors exactly like the app.
            var active = await shops.ListAsync(includeInactive: false, ct);
            var byShop = await accents.GetEffectiveAsync(active.Select(s => s.Id), ct);
            var shopAccents = active.Where(s => byShop.ContainsKey(s.Id))
                .ToDictionary(s => s.EtsyShopId.ToString(System.Globalization.CultureInfo.InvariantCulture), s => byShop[s.Id]);
            payload["color_config"] = (await colors.GetAsync(ct)).ToCardJson(shopAccents);
            return Results.Json(payload);
        });
        ha.MapGet("/shops/{shopId:long}/open-orders", async (long shopId, OrderQueries q, CancellationToken ct) =>
            await q.HaOpenOrdersAsync(shopId, ct) is { } payload ? Results.Json(payload) : Results.NotFound());

        // The HA card (homeassistant/www/etsy-orders-card.js, embedded at build time). The Orders page loads it for
        // the coloring engine it shares with the card. Public: it is the same file HA serves.
        endpoints.MapGet(OrderCardScript.Url, (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // pages link it with a content hash
                return Results.Bytes(OrderCardScript.Content, "text/javascript; charset=utf-8");
            })
            .AllowAnonymous();
    }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Orders", "orders", Icons.Material.Filled.LocalShipping, Order: 15),
        new("Packing slips", "packing-slips", Icons.Material.Filled.Receipt, Order: 16, Roles: "Admin,Editor"),
        new("API keys", "admin/api-keys", Icons.Material.Filled.Key, Order: 925, Roles: Roles.Admin, Group: NavGroups.Settings),
        new("Order colors", "admin/order-colors", Icons.Material.Filled.Palette, Order: 926, Roles: Roles.Admin, Group: NavGroups.Settings),
    ];
}

/// <summary>The Home Assistant card's script, embedded from homeassistant/www so the app and HA run the same file.</summary>
public static class OrderCardScript
{
    public const string Url = "/js/etsy-orders-card.js";
    public static readonly byte[] Content = Load();

    /// <summary>The URL with a content hash, so browsers pick up a new version after each deploy.</summary>
    public static readonly string VersionedUrl =
        Url.TrimStart('/') + "?v=" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Content))[..12];

    private static byte[] Load()
    {
        using var stream = typeof(OrderCardScript).Assembly.GetManifestResourceStream("etsy-orders-card.js")
                           ?? throw new InvalidOperationException("Embedded etsy-orders-card.js is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

public sealed class OrderPollQueue(IJobQueue queue)
{
    public Task<long?> PollShopAsync(int shopId, string? createdBy, string? correlationId = null, CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = PollShopOrdersJob.Type,
            ShopId = shopId,
            DedupeKey = $"{PollShopOrdersJob.Type}:{shopId}",
            CorrelationId = correlationId,
            Priority = 10, // ahead of listing imports: orders are time-sensitive
            MaxAttempts = 3, // the next scheduled poll retries anyway
            CreatedBy = createdBy,
        }, ct);
}

public sealed class PollShopOrdersJob(OrderPoller poller) : IJobHandler
{
    public const string Type = "etsy.orders.poll";
    public string JobType => Type;
    public string DisplayName => "Check for orders";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var shopId = context.ShopId ?? throw new JobPermanentFailureException("ShopId is required.");
        try
        {
            var result = await poller.PollShopAsync(shopId, ct);
            context.Result = Summary(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RetryLaterException)
        {
            await poller.RecordErrorAsync(shopId, ex.Message, CancellationToken.None);
            throw;
        }
    }
    /// <summary>"2 new, 3 updated", "no changes", or the size of the first (history) pull.</summary>
    internal static string Summary(OrderPollResult r)
    {
        if (r.WasBackfill) return $"{r.Received} order(s) in history";
        var updated = r.Received - r.New;
        return r.Received == 0 ? "no new or changed orders"
            : string.Join(", ", new[] { r.New > 0 ? $"{r.New} new" : null, updated > 0 ? $"{updated} updated" : null }.OfType<string>());
    }
}

/// <summary>The recurring poll: one job per connected shop.</summary>
public sealed class PollAllOrdersJob(IDbContextFactory<AppDbContext> dbFactory, OrderPollQueue queue) : IJobHandler
{
    public const string Type = "etsy.orders.poll-all";
    public string JobType => Type;
    public string DisplayName => "Poll orders (all shops)";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopIds = await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
            .Select(s => s.Id).ToListAsync(ct);
        foreach (var shopId in shopIds)
            await queue.PollShopAsync(shopId, "scheduler", $"job:{context.JobId}", ct);
        context.Result = $"{shopIds.Count} shop(s) queued";
    }
}

public sealed class PollOrdersOnShopConnected(OrderPollQueue queue) : IDomainEventHandler<ShopConnected>
{
    public Task HandleAsync(ShopConnected e, CancellationToken ct) => queue.PollShopAsync(e.ShopId, "shop-connected", ct: ct);
}

/// <summary>
/// Reads the buyer's first name for gift orders stored without one, so their gift slips print "- Kate" like Etsy's
/// (getUser, GET only). Queued at startup by <see cref="GiftBuyerNamesStartup"/> and whenever a shop is (re)connected,
/// which is how a shop's token gets the scope this needs. Orders that already have a name are left alone, so running it
/// again only costs calls for buyers Etsy didn't name before.
/// </summary>
public sealed class GiftBuyerNamesJob(IDbContextFactory<AppDbContext> dbFactory, OrderPoller poller) : IJobHandler
{
    public const string Type = "etsy.orders.gift-buyer-names";
    public string JobType => Type;
    public string DisplayName => "Read buyer names for gift slips";

    public static JobRequest Request(string createdBy) => new() { JobType = Type, DedupeKey = Type, CreatedBy = createdBy };

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        List<(int Id, string Name)> shops;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            shops = [.. (await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
                .Select(s => new { s.Id, s.Name }).ToListAsync(ct)).Select(s => (s.Id, s.Name))];

        int orders = 0, named = 0;
        var needReconnect = new List<string>();
        foreach (var (id, name) in shops)
        {
            var result = await poller.FillGiftBuyerNamesAsync(id, ct);
            orders += result.Orders;
            named += result.Named;
            if (!result.CanRead && result.Orders > 0) needReconnect.Add(name);
        }
        context.Result = (orders == 0 ? "every gift order has a buyer name" : $"{named} of {orders} gift order(s) named")
            + (needReconnect.Count == 0 ? "" : $"; reconnect {string.Join(" and ", needReconnect)} to allow buyer names");
    }
}

/// <summary>Queues <see cref="GiftBuyerNamesJob"/> at startup when gift orders lack a buyer name.</summary>
public sealed class GiftBuyerNamesStartup(IDbContextFactory<AppDbContext> dbFactory, IJobQueue queue,
    ILogger<GiftBuyerNamesStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Orders.AnyAsync(o => o.IsGift && o.GiftFromName == null && o.BuyerUserId > 0, ct))
                await queue.EnqueueAsync(GiftBuyerNamesJob.Request("system"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue reading buyer names for gift slips; it is tried again at the next start");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A (re)connected shop may now have the scope to read buyer names: fill in the ones missing.</summary>
public sealed class GiftBuyerNamesOnShopConnected(IJobQueue queue) : IDomainEventHandler<ShopConnected>
{
    public Task HandleAsync(ShopConnected e, CancellationToken ct) => queue.EnqueueAsync(GiftBuyerNamesJob.Request("shop-connected"), ct);
}
