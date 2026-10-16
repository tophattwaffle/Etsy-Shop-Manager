namespace EtsyShopManager.Core.Domain;

/// <summary>Key/value application setting stored in the database (editable in the UI).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }

    /// <summary>True when <see cref="Value"/> is protected with Data Protection.</summary>
    public bool IsEncrypted { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Well-known setting keys.</summary>
public static class SettingKeys
{
    public const string BaseUrl = "App.BaseUrl";
    public const string EtsyKeystring = "Etsy.Keystring";
    public const string EtsySharedSecret = "Etsy.SharedSecret";
    public const string SetupCompleted = "Setup.Completed";

    /// <summary>Seconds between order polls (default 300, as the old HA integration used).</summary>
    public const string OrdersPollIntervalSeconds = "Orders.PollIntervalSeconds";
}

/// <summary>Who changed what. Written for user, settings and shop changes.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}
