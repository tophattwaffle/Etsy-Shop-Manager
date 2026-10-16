using System.Security.Cryptography;
using System.Text;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record ApiKeyInfo(int Id, string Name, string Prefix, DateTime CreatedAtUtc, string? CreatedBy,
    DateTime? LastUsedAtUtc, DateTime? RevokedAtUtc);

/// <summary>
/// API keys for machine clients (Home Assistant). A key is shown once when created; only its SHA-256 is stored,
/// so a leaked database doesn't leak working keys.
/// </summary>
public sealed class ApiKeyService(IDbContextFactory<AppDbContext> dbFactory, IAuditLog audit, TimeProvider time)
{
    public const string KeyPrefix = "esm_";

    /// <summary>Only write "last used" this often per key, to keep polling from writing on every request.</summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    public async Task<(ApiKeyInfo Info, string Key)> CreateAsync(string name, string? createdBy, CancellationToken ct = default)
    {
        var key = KeyPrefix + Pkce.Base64Url(RandomNumberGenerator.GetBytes(32));
        var entity = new ApiKey
        {
            Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim(),
            Prefix = key[..(KeyPrefix.Length + 6)],
            Hash = Hash(key),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            CreatedBy = createdBy,
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("apikey.created", nameof(ApiKey), entity.Id.ToString(), entity.Name, createdBy, ct);
        return (ToInfo(entity), key);
    }

    public async Task<List<ApiKeyInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAtUtc).ToListAsync(ct);
        return keys.Select(ToInfo).ToList();
    }

    public async Task RevokeAsync(int id, string? userName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.ApiKeys.Where(k => k.Id == id && k.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAtUtc, now), ct);
        if (updated > 0) await audit.WriteAsync("apikey.revoked", nameof(ApiKey), id.ToString(), null, userName, ct);
    }

    /// <summary>True when the key exists and isn't revoked. Records when it was last used.</summary>
    public async Task<bool> ValidateAsync(string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Length > 200) return false;
        var hash = Hash(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash && k.RevokedAtUtc == null, ct);
        if (entity is null) return false;

        var now = time.GetUtcNow().UtcDateTime;
        if (entity.LastUsedAtUtc is null || entity.LastUsedAtUtc < now - LastUsedResolution)
        {
            entity.LastUsedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static ApiKeyInfo ToInfo(ApiKey k) =>
        new(k.Id, k.Name, k.Prefix, k.CreatedAtUtc, k.CreatedBy, k.LastUsedAtUtc, k.RevokedAtUtc);
}

/// <summary>
/// Requires a valid API key in <c>Authorization: Bearer esm_…</c> or <c>X-Api-Key: esm_…</c>. Used by the HA API
/// instead of the sign-in cookie.
/// </summary>
public sealed class ApiKeyEndpointFilter(ApiKeyService keys) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        string? key = null;
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(key)) key = request.Headers["X-Api-Key"].ToString().Trim();

        if (!await keys.ValidateAsync(key, context.HttpContext.RequestAborted))
            return Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }
}
