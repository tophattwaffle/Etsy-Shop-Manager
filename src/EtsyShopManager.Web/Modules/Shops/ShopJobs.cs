using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// Refreshes every connected shop's tokens. Etsy refresh tokens last ~90 days and each refresh issues a new one,
/// so running this regularly keeps shops connected even when nothing else calls Etsy.
/// </summary>
public sealed class TokenKeepAliveJob(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyTokenService tokens,
    ILogger<TokenKeepAliveJob> logger) : IJobHandler
{
    public const string Type = "etsy.tokens.keepalive";
    public string JobType => Type;

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopIds = await db.ShopTokens.Where(t => !t.NeedsReauth && t.Shop.IsActive).Select(t => t.ShopId).ToListAsync(ct);

        var failures = new List<Exception>();
        foreach (var shopId in shopIds)
        {
            try
            {
                await tokens.RefreshAsync(shopId, force: true, ct);
            }
            catch (EtsyReauthRequiredException)
            {
                // Already recorded and announced by the token service; nothing to retry.
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RetryLaterException)
            {
                logger.LogWarning(ex, "Token keep-alive failed for shop {ShopId}", shopId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Token refresh failed for one or more shops", failures);
    }
}

/// <summary>Fetches one shop's details from Etsy and updates the local record.</summary>
public sealed class ShopRefreshJob(ShopService shops) : IJobHandler
{
    public const string Type = "etsy.shop.refresh";
    public string JobType => Type;

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var shopId = context.ShopId ?? throw new JobPermanentFailureException("ShopId is required.");
        await shops.TestConnectionAsync(shopId, ct);
    }
}

/// <summary>Fans out one <see cref="ShopRefreshJob"/> per active shop.</summary>
public sealed class RefreshAllShopsJob(IDbContextFactory<AppDbContext> dbFactory, IJobQueue queue) : IJobHandler
{
    public const string Type = "etsy.shops.refresh-all";
    public string JobType => Type;

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopIds = await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
            .Select(s => s.Id).ToListAsync(ct);

        foreach (var shopId in shopIds)
        {
            await queue.EnqueueAsync(new JobRequest
            {
                JobType = ShopRefreshJob.Type,
                ShopId = shopId,
                DedupeKey = $"{ShopRefreshJob.Type}:{shopId}",
                CorrelationId = $"job:{context.JobId}",
                CreatedBy = "scheduler",
            }, ct);
        }
    }
}

/// <summary>Records a rejected refresh token in the audit log, so it is visible after the fact.</summary>
public sealed class AuditShopNeedsReauth(IAuditLog audit) : IDomainEventHandler<ShopNeedsReauth>
{
    public Task HandleAsync(ShopNeedsReauth e, CancellationToken ct) =>
        audit.WriteAsync("shop.needs-reauth", "Shop", e.ShopId.ToString(), e.Reason, "system", ct);
}
