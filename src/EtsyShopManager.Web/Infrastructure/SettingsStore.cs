using System.Collections.Concurrent;
using System.Security.Cryptography;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>DB-backed settings with an in-memory cache. Secrets are protected with Data Protection (DPAPI on Windows).</summary>
public sealed class SettingsStore(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<SettingsStore> logger) : ISettingsStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("EtsyShopManager.Settings.v1");
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        string? value = null;
        if (row?.Value is { } stored)
        {
            if (!row.IsEncrypted) value = stored;
            else
            {
                try { value = _protector.Unprotect(stored); }
                catch (CryptographicException ex)
                {
                    logger.LogError(ex, "Setting {Key} could not be decrypted (Data Protection keys changed?). Re-enter it.", key);
                }
            }
        }

        _cache[key] = value;
        return value;
    }

    public async Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new AppSetting { Key = key };
            db.Settings.Add(row);
        }

        row.Value = value is null ? null : encrypt ? _protector.Protect(value) : value;
        row.IsEncrypted = encrypt && value is not null;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        row.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
        _cache[key] = value;
    }

    public async Task<bool> HasValueAsync(string key, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await GetAsync(key, ct));

    public void ClearCache() => _cache.Clear();
}

/// <summary>Reads the Etsy keystring and shared secret from settings.</summary>
public sealed class EtsyCredentialsProvider(ISettingsStore settings) : IEtsyCredentialsProvider
{
    public async Task<EtsyCredentials?> GetAsync(CancellationToken ct = default)
    {
        var keystring = await settings.GetAsync(SettingKeys.EtsyKeystring, ct);
        var secret = await settings.GetAsync(SettingKeys.EtsySharedSecret, ct);
        return string.IsNullOrWhiteSpace(keystring) || string.IsNullOrWhiteSpace(secret)
            ? null
            : new EtsyCredentials(keystring.Trim(), secret.Trim());
    }
}

public sealed class AuditLog(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<AuditLog> logger) : IAuditLog
{
    public async Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
        string? userName = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AuditEntries.Add(new AuditEntry
            {
                AtUtc = time.GetUtcNow().UtcDateTime,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                UserName = userName,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Auditing must never break the action being audited.
            logger.LogError(ex, "Failed to write audit entry {Action}", action);
        }
    }
}

/// <summary>Stores the Etsy write journal (see <see cref="IEtsyWriteJournal"/>).</summary>
public sealed class EtsyWriteJournal(IDbContextFactory<AppDbContext> dbFactory, ILogger<EtsyWriteJournal> logger) : IEtsyWriteJournal
{
    public async Task RecordAsync(EtsyWriteRecord record, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.EtsyWriteLogs.Add(new EtsyWriteLog
            {
                AtUtc = record.AtUtc,
                Method = record.Method,
                Path = record.Path,
                RequestBody = record.RequestBody,
                StatusCode = record.StatusCode,
                ResponseBody = record.ResponseBody,
                Error = record.Error,
                DurationMs = record.DurationMs,
                Source = record.Source,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording must never break the write it records.
            logger.LogError(ex, "Failed to record Etsy write {Method} {Path}", record.Method, record.Path);
        }
    }
}
