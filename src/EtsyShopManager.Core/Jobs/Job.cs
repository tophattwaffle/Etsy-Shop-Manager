namespace EtsyShopManager.Core.Jobs;

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// A unit of background work persisted in the database, so it survives restarts.
/// All Etsy writes go through jobs so they stay under rate limits.
/// </summary>
public class Job
{
    public long Id { get; set; }
    public string JobType { get; set; } = "";
    public string? PayloadJson { get; set; }
    public JobStatus Status { get; set; }

    /// <summary>Higher runs first.</summary>
    public int Priority { get; set; }

    /// <summary>Optional shop the job acts on.</summary>
    public int? ShopId { get; set; }

    /// <summary>If set, only one Pending/Running job with this key can exist.</summary>
    public string? DedupeKey { get; set; }

    /// <summary>Groups related jobs (e.g. all jobs from one template push).</summary>
    public string? CorrelationId { get; set; }

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime RunAfterUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public string? LastError { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>Short outcome set by the handler, e.g. "2 new, 5 updated". Shown on the Jobs page and in notifications.</summary>
    public string? Result { get; set; }
}

/// <summary>Tracks when a recurring job was last enqueued.</summary>
public class RecurringJobState
{
    public string Name { get; set; } = "";
    public DateTime? LastEnqueuedAtUtc { get; set; }
    public long? LastJobId { get; set; }
}
