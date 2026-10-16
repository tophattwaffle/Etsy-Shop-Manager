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

        services.AddJobHandler<PollShopOrdersJob>();
        services.AddJobHandler<PollAllOrdersJob>();
        services.AddScoped<IDomainEventHandler<ShopConnected>, PollOrdersOnShopConnected>();

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
        ha.MapGet("/open-orders", async (OrderQueries q, OrderColorService colors, CancellationToken ct) =>
        {
            var payload = await q.HaOpenOrdersAllAsync(ct);
            // The order color list rides along so the HA card colors exactly like the Orders page.
            payload["color_config"] = (await colors.GetAsync(ct)).ToCardJson();
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
        new("API keys", "admin/api-keys", Icons.Material.Filled.Key, Order: 925, Roles: Roles.Admin),
        new("Order colors", "admin/order-colors", Icons.Material.Filled.Palette, Order: 926, Roles: Roles.Admin),
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
