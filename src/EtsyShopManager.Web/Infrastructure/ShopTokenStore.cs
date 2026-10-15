using System.Security.Cryptography;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>Stores Etsy OAuth tokens per shop, encrypted with Data Protection.</summary>
public sealed class ShopTokenStore(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<ShopTokenStore> logger) : IEtsyTokenStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("EtsyShopManager.Tokens.v1");

    public async Task<EtsyTokenSet?> GetAsync(int shopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ShopTokens.AsNoTracking().FirstOrDefaultAsync(t => t.ShopId == shopId, ct);
        if (row is null) return null;

        try
        {
            return new EtsyTokenSet
            {
                AccessToken = _protector.Unprotect(row.EncryptedAccessToken),
                RefreshToken = _protector.Unprotect(row.EncryptedRefreshToken),
                AccessTokenExpiresAtUtc = row.AccessTokenExpiresAtUtc,
                RefreshTokenIssuedAtUtc = row.RefreshTokenIssuedAtUtc,
                Scopes = row.Scopes,
                NeedsReauth = row.NeedsReauth,
            };
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Tokens for shop {ShopId} could not be decrypted; the shop must be reconnected", shopId);
            return null;
        }
    }

    public async Task SaveAsync(int shopId, EtsyTokenSet tokens, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ShopTokens.FirstOrDefaultAsync(t => t.ShopId == shopId, ct);
        if (row is null)
        {
            row = new ShopToken { ShopId = shopId };
            db.ShopTokens.Add(row);
        }

        row.EncryptedAccessToken = _protector.Protect(tokens.AccessToken);
        row.EncryptedRefreshToken = _protector.Protect(tokens.RefreshToken);
        row.AccessTokenExpiresAtUtc = tokens.AccessTokenExpiresAtUtc;
        row.RefreshTokenIssuedAtUtc = tokens.RefreshTokenIssuedAtUtc;
        row.Scopes = tokens.Scopes;
        row.LastRefreshedAtUtc = time.GetUtcNow().UtcDateTime;
        row.NeedsReauth = false;
        row.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkReauthRequiredAsync(int shopId, string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ShopTokens.Where(t => t.ShopId == shopId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.NeedsReauth, true)
            .SetProperty(t => t.LastError, reason), ct);
    }
}
