using System.Collections.Concurrent;
using System.Net;
using EtsyShopManager.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy;

/// <summary>
/// Hands out valid access tokens per shop, refreshing them (one refresh at a time per shop) when they are
/// about to expire. A rejected refresh token marks the shop as needing reconnection.
/// </summary>
public sealed class EtsyTokenService(
    IEtsyTokenStore store,
    EtsyOAuthClient oauth,
    IDomainEventPublisher events,
    IOptions<EtsyOptions> options,
    TimeProvider time,
    ILogger<EtsyTokenService> logger)
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<string> GetAccessTokenAsync(int shopId, CancellationToken ct = default)
    {
        var tokens = await store.GetAsync(shopId, ct) ?? throw new EtsyReauthRequiredException(shopId, "no token stored");
        if (tokens.NeedsReauth) throw new EtsyReauthRequiredException(shopId, "refresh token was rejected earlier");

        if (tokens.AccessTokenExpiresAtUtc - options.Value.AccessTokenRefreshSkew > time.GetUtcNow().UtcDateTime)
            return tokens.AccessToken;

        return (await RefreshAsync(shopId, force: false, ct)).AccessToken;
    }

    /// <summary>Refreshes the shop's tokens. With force=false it is skipped if another caller just refreshed.</summary>
    public async Task<EtsyTokenSet> RefreshAsync(int shopId, bool force, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(shopId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await store.GetAsync(shopId, ct) ?? throw new EtsyReauthRequiredException(shopId, "no token stored");
            if (current.NeedsReauth) throw new EtsyReauthRequiredException(shopId, "refresh token was rejected earlier");

            if (!force && current.AccessTokenExpiresAtUtc - options.Value.AccessTokenRefreshSkew > time.GetUtcNow().UtcDateTime)
                return current;

            EtsyTokenSet refreshed;
            try
            {
                refreshed = await oauth.RefreshAsync(current.RefreshToken, current.Scopes, ct);
            }
            catch (EtsyOAuthException ex) when (ex.Error == "invalid_grant"
                                                 || ex.StatusCode is HttpStatusCode.Unauthorized)
            {
                var reason = ex.Description ?? ex.Error ?? "refresh token rejected";
                logger.LogWarning("Etsy refresh token for shop {ShopId} was rejected: {Reason}", shopId, reason);
                await store.MarkReauthRequiredAsync(shopId, reason, ct);
                await events.PublishAsync(new ShopNeedsReauth(shopId, reason), ct);
                throw new EtsyReauthRequiredException(shopId, reason);
            }

            await store.SaveAsync(shopId, refreshed, ct);
            logger.LogInformation("Refreshed Etsy token for shop {ShopId}; expires {Expires:u}", shopId, refreshed.AccessTokenExpiresAtUtc);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }
}
