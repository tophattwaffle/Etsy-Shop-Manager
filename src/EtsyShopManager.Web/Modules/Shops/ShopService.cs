using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Shops;

public sealed record ShopSummary(
    int Id,
    long EtsyShopId,
    string Name,
    string? Title,
    string? Url,
    string? IconUrl,
    string? CurrencyCode,
    bool IsActive,
    DateTime ConnectedAtUtc,
    DateTime? LastSyncedAtUtc,
    bool HasToken,
    bool NeedsReauth,
    DateTime? AccessTokenExpiresAtUtc,
    DateTime? TokenLastRefreshedAtUtc,
    DateTime? RefreshTokenIssuedAtUtc,
    string? TokenError)
{
    public bool IsHealthy => IsActive && HasToken && !NeedsReauth;
}

public sealed class ShopService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEtsyTokenStore tokenStore,
    EtsyApiClient etsy,
    IJobQueue jobs,
    IAuditLog audit,
    IDomainEventPublisher events,
    TimeProvider time)
{
    public async Task<List<ShopSummary>> ListAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Shops.AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new ShopSummary(
                s.Id, s.EtsyShopId, s.Name, s.Title, s.Url, s.IconUrl, s.CurrencyCode, s.IsActive,
                s.ConnectedAtUtc, s.LastSyncedAtUtc,
                s.Token != null,
                s.Token != null && s.Token.NeedsReauth,
                s.Token != null ? s.Token.AccessTokenExpiresAtUtc : null,
                s.Token != null ? s.Token.LastRefreshedAtUtc : null,
                s.Token != null ? s.Token.RefreshTokenIssuedAtUtc : null,
                s.Token != null ? s.Token.LastError : null))
            .ToListAsync(ct);
    }

    /// <summary>Called by the OAuth callback. Creates the shop, or reactivates it if it was connected before.</summary>
    public async Task<(Shop Shop, bool IsReconnect)> UpsertConnectedShopAsync(EtsyShop info, EtsyTokenSet tokens,
        string? userName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        var shop = await db.Shops.FirstOrDefaultAsync(s => s.EtsyShopId == info.ShopId, ct);
        var isReconnect = shop is not null;
        if (shop is null)
        {
            shop = new Shop { EtsyShopId = info.ShopId, ConnectedAtUtc = now };
            db.Shops.Add(shop);
        }

        Apply(shop, info, now);
        shop.EtsyUserId = tokens.EtsyUserId ?? info.UserId;
        shop.IsActive = true;
        if (isReconnect) shop.ConnectedAtUtc = now;
        await db.SaveChangesAsync(ct);

        await tokenStore.SaveAsync(shop.Id, tokens, ct);
        await audit.WriteAsync(isReconnect ? "shop.reconnected" : "shop.connected", nameof(Shop), shop.Id.ToString(),
            $"{shop.Name} (Etsy shop {shop.EtsyShopId})", userName, ct);
        await events.PublishAsync(new ShopConnected(shop.Id, shop.EtsyShopId, shop.Name, isReconnect), ct);
        return (shop, isReconnect);
    }

    /// <summary>Removes the tokens and marks the shop inactive. Local data is kept for a later reconnect.</summary>
    public async Task DisconnectAsync(int shopId, string? userName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Shops.Include(s => s.Token).FirstOrDefaultAsync(s => s.Id == shopId, ct);
        if (shop is null) return;

        if (shop.Token is not null) db.ShopTokens.Remove(shop.Token);
        shop.IsActive = false;
        await db.SaveChangesAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        await db.Jobs.Where(j => j.ShopId == shopId && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Cancelled)
                .SetProperty(j => j.CompletedAtUtc, now)
                .SetProperty(j => j.LastError, "Shop disconnected"), ct);

        await audit.WriteAsync("shop.disconnected", nameof(Shop), shopId.ToString(), shop.Name, userName, ct);
        await events.PublishAsync(new ShopDisconnected(shopId, shop.Name), ct);
    }

    /// <summary>Calls Etsy right now with the shop's token (refreshing it if needed) and updates the shop details.</summary>
    public async Task<EtsyShop> TestConnectionAsync(int shopId, CancellationToken ct = default)
    {
        var etsyShopId = await GetEtsyShopIdAsync(shopId, ct);
        var info = await etsy.GetShopAsync(etsyShopId, EtsyAuth.ForShop(shopId), ct);
        await ApplyShopInfoAsync(shopId, info, ct);
        return info;
    }

    public Task<long?> RequestRefreshAsync(int shopId, string? userName, CancellationToken ct = default) =>
        jobs.EnqueueAsync(new JobRequest
        {
            JobType = ShopRefreshJob.Type,
            ShopId = shopId,
            DedupeKey = $"{ShopRefreshJob.Type}:{shopId}",
            CreatedBy = userName,
        }, ct);

    public async Task ApplyShopInfoAsync(int shopId, EtsyShop info, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.Id == shopId, ct);
        if (shop is null) return;
        Apply(shop, info, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    public async Task<long> GetEtsyShopIdAsync(int shopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Shops.Where(s => s.Id == shopId).Select(s => (long?)s.EtsyShopId).FirstOrDefaultAsync(ct)
               ?? throw new JobPermanentFailureException($"Shop {shopId} does not exist.");
    }

    private static void Apply(Shop shop, EtsyShop info, DateTime now)
    {
        shop.Name = info.ShopName;
        shop.Title = info.Title;
        shop.Url = info.Url;
        shop.CurrencyCode = info.CurrencyCode;
        shop.IconUrl = info.IconUrl;
        shop.LastSyncedAtUtc = now;
    }
}
