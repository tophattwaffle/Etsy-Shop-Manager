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

    /// <summary>Hours between scheduled backup job runs (default 24).</summary>
    public const string BackupsScheduleIntervalHours = "Backups.ScheduleIntervalHours";

    /// <summary>Days to retain restorable backups before pruning (default 30, 0 = keep indefinitely).</summary>
    public const string BackupsRetentionDays = "Backups.RetentionDays";

    /// <summary>Maximum number of restorable backups to keep (default 10, 0 = unlimited).</summary>
    public const string BackupsMaxCount = "Backups.MaxCount";

    /// <summary>Configured backup directory path (null or empty = default under Storage:DataRoot).</summary>
    public const string BackupsDirectory = "Backups.Directory";
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

/// <summary>
/// A write the app sent to Etsy and Etsy's reply (Admin → Etsy writes). Kept so a failed or surprising push can be
/// traced to the exact request; file uploads are summarised, never stored. Old rows are removed by the jobs cleanup.
/// </summary>
public class EtsyWriteLog
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? RequestBody { get; set; }

    /// <summary>Null when no response arrived (network error, timeout); see <see cref="Error"/>.</summary>
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    /// <summary>What made the call, e.g. "job 229 etsy.listing.push".</summary>
    public string? Source { get; set; }
}
