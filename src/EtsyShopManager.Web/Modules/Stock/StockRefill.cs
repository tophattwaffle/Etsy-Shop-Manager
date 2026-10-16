using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Infrastructure.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Stock;

public sealed record RefillOutcome(IReadOnlyList<string> Changes, string? Problem)
{
    public string Summary => Problem ?? (Changes.Count == 0 ? "stock is above the threshold" : string.Join(", ", Changes));
}

/// <summary>
/// Auto-refill: keeps each variation of a listing from selling out. When an offering's stock drops below the listing's
/// threshold it is set to the refill-to value, while the listing is still active (a sold-out listing is deactivated by
/// Etsy and reactivating it costs a renewal fee, so the threshold is what prevents that). The write starts from a fresh
/// inventory read and changes only quantities.
/// </summary>
public sealed class StockRefiller(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyApiClient etsy,
    IDomainEventPublisher events,
    IAuditLog audit,
    TimeProvider time,
    ILogger<StockRefiller> logger)
{
    /// <summary>Etsy's maximum quantity per offering.</summary>
    public const int MaxQuantity = 999;

    public async Task SaveSettingsAsync(int listingId, bool enabled, int threshold, int refillTo, string? user,
        CancellationToken ct = default)
    {
        if (threshold < 1) throw new ArgumentException("The threshold must be at least 1.");
        if (refillTo <= threshold) throw new ArgumentException("Refill-to must be more than the threshold.");
        if (refillTo > MaxQuantity) throw new ArgumentException($"Etsy allows at most {MaxQuantity} per variation.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new InvalidOperationException($"Listing {listingId} not found.");
        var row = await db.ListingRefills.FirstOrDefaultAsync(r => r.ListingId == listingId, ct);
        if (row is null) db.ListingRefills.Add(row = new ListingRefill { ListingId = listingId });
        row.Enabled = enabled;
        row.Threshold = threshold;
        row.RefillTo = refillTo;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        row.UpdatedBy = user;
        if (!enabled) row.Problem = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("stock.refill-settings", nameof(Listing), listing.EtsyListingId.ToString(),
            enabled ? $"on: below {threshold}, refill to {refillTo}" : "off", user, ct);
    }

    public async Task<RefillOutcome> RefillListingAsync(int listingId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.ListingRefills.Include(r => r.Listing).FirstOrDefaultAsync(r => r.ListingId == listingId, ct);
        if (settings is not { Enabled: true }) return new RefillOutcome([], "auto-refill is off for this listing");
        var listing = settings.Listing;
        var now = time.GetUtcNow().UtcDateTime;
        settings.LastCheckedAtUtc = now;

        var auth = EtsyAuth.ForShop(listing.ShopId);
        var inventory = await etsy.GetListingInventoryAsync(listing.EtsyListingId, includeListing: true, auth, ct);
        var state = inventory.Listing?.State ?? listing.State;
        if (state != "active")
        {
            settings.Problem = $"Not refilled: the listing is {ListingFormat.StateLabel(state)} on Etsy.";
            settings.LastResult = settings.Problem;
            await db.SaveChangesAsync(ct);
            return new RefillOutcome([], settings.Problem);
        }

        var plan = Plan(inventory, settings.Threshold, settings.RefillTo);
        if (plan.Count == 0)
        {
            settings.Problem = null;
            settings.LastResult = "stock is above the threshold";
            await db.SaveChangesAsync(ct);
            return new RefillOutcome([], null);
        }

        var body = InventoryWriter.FromEtsy(inventory, (product, offering, update) =>
        {
            if (plan.ContainsKey(offering.OfferingId)) update.Quantity = settings.RefillTo;
            return update;
        });
        var updated = await etsy.UpdateListingInventoryAsync(listing.EtsyListingId, body, auth, ct);

        // Keep the local copy current (Etsy issues new product/offering ids on every inventory write).
        var tracked = await db.Listings.Include(l => l.Products).ThenInclude(p => p.Offerings)
            .FirstAsync(l => l.Id == listingId, ct);
        ListingMapper.SyncInventory(tracked, updated);

        var changes = plan.Values.Select(p => $"{p.Label}: {p.From} → {settings.RefillTo}").ToList();
        settings.Problem = null;
        settings.LastRefilledAtUtc = now;
        settings.LastResult = string.Join(", ", changes);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Refilled listing {ListingId}: {Changes}", listing.EtsyListingId, settings.LastResult);
        await audit.WriteAsync("stock.refilled", nameof(Listing), listing.EtsyListingId.ToString(), settings.LastResult, "system", ct);
        await events.PublishAsync(new StockRefilled(listing.ShopId, listingId, listing.EtsyListingId, changes), ct);
        return new RefillOutcome(changes, null);
    }

    /// <summary>
    /// The offerings to refill (by offering id): enabled ones below the threshold. When quantity doesn't vary by a
    /// property Etsy keeps one quantity for all variations, so then all of them are refilled together.
    /// </summary>
    public static Dictionary<long, (string Label, int From)> Plan(EtsyInventory inventory, int threshold, int refillTo)
    {
        var offerings = inventory.Products.Where(p => !p.IsDeleted)
            .SelectMany(p => p.Offerings.Where(o => !o.IsDeleted).Select(o => (Product: p, Offering: o)))
            .ToList();
        var low = offerings.Where(x => x.Offering.IsEnabled && x.Offering.Quantity < threshold && x.Offering.Quantity < refillTo).ToList();
        if (low.Count == 0) return [];

        var shared = (inventory.QuantityOnProperty ?? []).Count == 0;
        var targets = shared ? offerings.Where(x => x.Offering.Quantity < refillTo).ToList() : low;
        return targets.ToDictionary(x => x.Offering.OfferingId, x => (Label(x.Product), x.Offering.Quantity));
    }

    private static string Label(EtsyProduct p) =>
        p.PropertyValues.Count == 0 ? "stock" : string.Join(" / ", p.PropertyValues.SelectMany(v => v.Values ?? []));

    /// <summary>
    /// Finds refill-enabled active listings of a shop that need a refill, from a bulk read (about one call per 100
    /// active listings), and returns their local ids. Each is then refilled by its own job with a fresh read.
    /// </summary>
    public async Task<List<int>> FindLowStockAsync(int shopId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var enabled = await db.ListingRefills.AsNoTracking()
            .Where(r => r.Enabled && r.Listing.ShopId == shopId && !r.Listing.IsMissingOnEtsy)
            .Select(r => new { r.ListingId, r.Listing.EtsyListingId, r.Threshold, r.RefillTo })
            .ToListAsync(ct);
        if (enabled.Count == 0) return [];
        var etsyShopId = await db.Shops.Where(s => s.Id == shopId).Select(s => s.EtsyShopId).FirstAsync(ct);

        var byEtsyId = enabled.ToDictionary(e => e.EtsyListingId);
        var due = new List<int>();
        for (var offset = 0; ; offset += EtsyApiClient.MaxListingsPageSize)
        {
            var page = await etsy.GetListingsByShopAsync(etsyShopId, EtsyListingStates.Active, offset,
                EtsyApiClient.MaxListingsPageSize, EtsyAuth.ForShop(shopId), ct);
            foreach (var (listing, _) in page.Results)
                if (byEtsyId.TryGetValue(listing.ListingId, out var e) && listing.Inventory is { } inv
                    && Plan(inv, e.Threshold, e.RefillTo).Count > 0)
                    due.Add(e.ListingId);
            if (page.Results.Count < EtsyApiClient.MaxListingsPageSize || offset + page.Results.Count >= page.Count) break;
        }
        return due;
    }
}

public sealed record RefillPayload(int ListingId);

public sealed class RefillQueue(IJobQueue queue)
{
    public Task<long?> RefillAsync(int shopId, int listingId, string? createdBy, string? correlationId = null,
        CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = RefillListingJob.Type,
            ShopId = shopId,
            Payload = new RefillPayload(listingId),
            DedupeKey = $"{RefillListingJob.Type}:{listingId}",
            CorrelationId = correlationId,
            Priority = 15, // right after order checks: stock is time-sensitive
            CreatedBy = createdBy,
        }, ct);
}

/// <summary>Checks one listing and refills the variations that are running low.</summary>
public sealed class RefillListingJob(StockRefiller refiller) : IJobHandler
{
    public const string Type = "etsy.stock.refill";
    public string JobType => Type;
    public string DisplayName => "Stock refill";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var listingId = context.GetPayload<RefillPayload>()?.ListingId ?? throw new JobPermanentFailureException("ListingId is required.");
        context.Result = (await refiller.RefillListingAsync(listingId, ct)).Summary;
    }
}

/// <summary>Hourly: finds refill-enabled listings running low in every shop and queues a refill for each.</summary>
public sealed class RefillSweepJob(IDbContextFactory<AppDbContext> dbFactory, StockRefiller refiller, RefillQueue queue) : IJobHandler
{
    public const string Type = "etsy.stock.refill-sweep";
    public string JobType => Type;
    public string DisplayName => "Stock refill check (all shops)";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopIds = await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
            .Where(s => db.ListingRefills.Any(r => r.Enabled && r.Listing.ShopId == s.Id))
            .Select(s => s.Id).ToListAsync(ct);
        var queued = 0;
        foreach (var shopId in shopIds)
            foreach (var listingId in await refiller.FindLowStockAsync(shopId, ct))
                if (await queue.RefillAsync(shopId, listingId, "scheduler", $"job:{context.JobId}", ct) is not null) queued++;
        context.Result = queued == 0 ? "nothing running low" : $"{queued} listing(s) queued for refill";
    }
}

/// <summary>A new order: re-check the refill-enabled listings it contains (sales lower their stock).</summary>
public sealed class RefillOnOrderReceived(IDbContextFactory<AppDbContext> dbFactory, RefillQueue queue)
    : IDomainEventHandler<OrderReceived>
{
    public async Task HandleAsync(OrderReceived e, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var etsyListingIds = await db.OrderItems.Where(i => i.OrderId == e.OrderId && i.EtsyListingId != null)
            .Select(i => i.EtsyListingId!.Value).Distinct().ToListAsync(ct);
        var listingIds = await db.ListingRefills
            .Where(r => r.Enabled && r.Listing.ShopId == e.ShopId && etsyListingIds.Contains(r.Listing.EtsyListingId))
            .Select(r => r.ListingId).ToListAsync(ct);
        foreach (var listingId in listingIds)
            await queue.RefillAsync(e.ShopId, listingId, "new order", $"order:{e.EtsyReceiptId}", ct);
    }
}

/// <summary>Phase 3: the listing editor's push path, and auto-refill.</summary>
public sealed class EditorModule : IModule
{
    public string Name => "Editor";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ListingEditor>();
        services.AddSingleton<StockRefiller>();
        services.AddSingleton<RefillQueue>();
        services.AddJobHandler<PushListingJob>();
        services.AddJobHandler<RefillListingJob>();
        services.AddJobHandler<RefillSweepJob>();
        services.AddScoped<IDomainEventHandler<OrderReceived>, RefillOnOrderReceived>();
        services.AddRecurringJob(new RecurringJobDefinition("Stock refill check (all shops)", RefillSweepJob.Type, TimeSpan.FromHours(1))
        {
            InitialDelay = TimeSpan.FromMinutes(5),
        });
    }
}
