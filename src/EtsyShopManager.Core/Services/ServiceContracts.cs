namespace EtsyShopManager.Core.Services;

/// <summary>Database-backed application settings with optional encryption for secrets.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default);
    Task<bool> HasValueAsync(string key, CancellationToken ct = default);
}

public interface IAuditLog
{
    Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
        string? userName = null, CancellationToken ct = default);
}
