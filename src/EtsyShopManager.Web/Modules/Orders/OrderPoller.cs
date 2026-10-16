using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record OrderPollResult(int Received, int New, bool WasBackfill);

/// <summary>
/// Pulls a shop's receipts changed since the last poll (all of them the first time) into the local DB.
/// <para>
/// Idempotent: orders and items are matched by Etsy id. Resumable: the offset is saved after every page, so an
/// interrupted backfill continues where it stopped.
/// </para>
/// <para>
/// No gaps: pages are read in creation order, which a receipt being modified mid-poll doesn't change (in update
/// order it would jump to the end and shift the rest, so one could be skipped). A receipt modified during a poll is
/// picked up by the next one, which asks for changes since this poll <i>started</i>, minus a small overlap for
/// clock differences between this host and Etsy.
/// </para>
/// </summary>
public sealed class OrderPoller(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyApiClient etsy,
    IDomainEventPublisher events,
    TimeProvider time,
    ILogger<OrderPoller> logger)
{
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    public int PageSize { get; set; } = EtsyApiClient.MaxReceiptsPageSize;

    public async Task<OrderPollResult> PollShopAsync(int shopId, CancellationToken ct)
    {
        long etsyShopId;
        DateTime? since;
        int offset;
        bool backfill;
        DateTime pollStarted;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct)
                       ?? throw new JobPermanentFailureException($"Shop {shopId} does not exist.");
            if (!shop.IsActive) throw new JobPermanentFailureException($"Shop {shop.Name} is disconnected.");
            etsyShopId = shop.EtsyShopId;

            var sync = await db.ShopOrderSyncStates.FirstOrDefaultAsync(s => s.ShopId == shopId, ct);
            if (sync is null)
            {
                db.ShopOrderSyncStates.Add(sync = new ShopOrderSyncState { ShopId = shopId });
                await db.SaveChangesAsync(ct);
            }
            since = sync.ModifiedSinceUtc;
            offset = sync.Offset;
            backfill = sync.BackfillCompletedAtUtc is null;

            // A resumed poll keeps its original start, so changes made since then are still covered next time.
            if (offset == 0 || sync.PollStartedAtUtc is null)
            {
                sync.PollStartedAtUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
            }
            pollStarted = sync.PollStartedAtUtc.Value;
        }

        var auth = EtsyAuth.ForShop(shopId);
        int received = 0, created = 0;
        while (true)
        {
            var page = await etsy.GetShopReceiptsAsync(etsyShopId, since, offset, PageSize, auth, ct);
            created += await UpsertPageAsync(shopId, page.Results, raiseEvents: !backfill, ct);
            received += page.Results.Count;

            offset += page.Results.Count;
            var done = page.Results.Count < PageSize || offset >= page.Count;
            await SaveProgressAsync(shopId, done ? 0 : offset, done ? pollStarted - Overlap : since, done, ct);
            if (done) break;
        }

        if (backfill || created > 0)
            logger.LogInformation("Order poll for shop {ShopId}: {Received} receipt(s), {New} new{Backfill}",
                shopId, received, created, backfill ? " (initial backfill)" : "");
        return new OrderPollResult(received, created, backfill);
    }

    public async Task RecordErrorAsync(int shopId, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        await db.ShopOrderSyncStates.Where(s => s.ShopId == shopId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastError, error.Length > 2000 ? error[..2000] : error)
            .SetProperty(x => x.LastErrorAtUtc, now), ct);
    }

    private async Task SaveProgressAsync(int shopId, int offset, DateTime? since, bool done, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sync = await db.ShopOrderSyncStates.FirstAsync(s => s.ShopId == shopId, ct);
        sync.Offset = offset;
        if (done)
        {
            var now = time.GetUtcNow().UtcDateTime;
            sync.ModifiedSinceUtc = since;
            sync.LastPolledAtUtc = now;
            sync.BackfillCompletedAtUtc ??= now;
            sync.LastError = null;
            sync.LastErrorAtUtc = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> UpsertPageAsync(int shopId, List<EtsyRaw<EtsyReceipt>> page, bool raiseEvents,
        CancellationToken ct)
    {
        if (page.Count == 0) return 0;
        var now = time.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = page.Select(r => r.Value.ReceiptId).ToList();
        var existing = await db.Orders.Include(o => o.Items)
            .Where(o => ids.Contains(o.EtsyReceiptId))
            .ToDictionaryAsync(o => o.EtsyReceiptId, ct);

        var received = new List<Order>();
        var created = 0;
        foreach (var (receipt, raw) in page)
        {
            var isNew = !existing.TryGetValue(receipt.ReceiptId, out var order);
            if (order is null)
            {
                order = new Order { ShopId = shopId, FirstSeenAtUtc = now };
                db.Orders.Add(order);
                existing[receipt.ReceiptId] = order;
                created++;
            }
            var wasPaid = !isNew && order.IsPaid;
            OrderMapper.Apply(order, receipt, raw, now);
            if (order.IsPaid && !wasPaid) received.Add(order);
        }

        await ResolveImagesAsync(db, shopId, existing.Values.SelectMany(o => o.Items).Where(i => i.ImageUrl is null).ToList(), ct);
        await db.SaveChangesAsync(ct);

        if (raiseEvents)
            foreach (var order in received)
                await events.PublishAsync(new OrderReceived(shopId, order.Id, order.EtsyReceiptId), ct);
        return created;
    }

    /// <summary>
    /// Fills in each item's photo URL (Etsy CDN): the transaction's own listing image from the imported listings
    /// when possible, otherwise one batch call for listings that aren't imported (e.g. deleted ones).
    /// </summary>
    private async Task ResolveImagesAsync(AppDbContext db, int shopId, List<OrderItem> items, CancellationToken ct)
    {
        var withListing = items.Where(i => i.EtsyListingId is not null).ToList();
        if (withListing.Count == 0) return;

        var listingIds = withListing.Select(i => i.EtsyListingId!.Value).Distinct().ToList();
        var local = await db.ListingImages.AsNoTracking()
            .Where(img => listingIds.Contains(img.Listing.EtsyListingId))
            .Select(img => new { img.Listing.EtsyListingId, img.EtsyImageId, img.Rank, Url = img.SourceThumbUrl ?? img.SourceUrl })
            .ToListAsync(ct);
        var byListing = local.GroupBy(x => x.EtsyListingId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Rank).ToList());

        var missing = new List<OrderItem>();
        foreach (var item in withListing)
        {
            if (byListing.TryGetValue(item.EtsyListingId!.Value, out var images) && images.Count > 0)
                item.ImageUrl = (images.FirstOrDefault(x => x.EtsyImageId == item.ListingImageId) ?? images[0]).Url;
            else
                missing.Add(item);
        }
        if (missing.Count == 0) return;

        try
        {
            foreach (var chunk in missing.Select(i => i.EtsyListingId!.Value).Distinct().Chunk(EtsyApiClient.MaxBatchListingIds))
            {
                var listings = await etsy.GetListingsByIdsAsync(chunk, EtsyAuth.ForShop(shopId), ct);
                var found = listings.ToDictionary(l => l.ListingId, l => l.Images ?? []);
                foreach (var item in missing)
                {
                    if (!found.TryGetValue(item.EtsyListingId!.Value, out var images) || images.Count == 0) continue;
                    var image = images.FirstOrDefault(x => x.ListingImageId == item.ListingImageId)
                                ?? images.OrderBy(x => x.Rank).First();
                    item.ImageUrl = OrderMapper.ThumbnailUrl(image);
                }
            }
        }
        catch (EtsyApiException ex)
        {
            // Photos are cosmetic: keep the order, try again on a later poll.
            logger.LogWarning("Could not look up photos for {Count} order item(s): {Message}", missing.Count, ex.Message);
        }
    }
}
