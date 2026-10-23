using System.Text.Json;

namespace EtsyShopManager.Core.Jobs;

/// <summary>Request to enqueue a job.</summary>
public sealed record JobRequest
{
    public required string JobType { get; init; }
    public object? Payload { get; init; }
    public int? ShopId { get; init; }
    public string? DedupeKey { get; init; }
    public string? CorrelationId { get; init; }
    public DateTime? RunAfterUtc { get; init; }
    public int Priority { get; init; }
    public int MaxAttempts { get; init; } = 5;
    public string? CreatedBy { get; init; }
}

public interface IJobQueue
{
    /// <summary>Enqueues a job. Returns null when skipped because an active job has the same DedupeKey.</summary>
    Task<long?> EnqueueAsync(JobRequest request, CancellationToken ct = default);

    /// <summary>Puts a Failed or Cancelled job back to Pending with its attempts reset.</summary>
    Task<bool> RetryAsync(long jobId, CancellationToken ct = default);

    /// <summary>Cancels a Pending job. Running jobs cannot be cancelled.</summary>
    Task<bool> CancelAsync(long jobId, CancellationToken ct = default);
}

/// <summary>Executes one job type. Register implementations in DI (typically from a module).</summary>
public interface IJobHandler
{
    string JobType { get; }

    /// <summary>What people call this job, e.g. "Check for orders". Used in notifications.</summary>
    string DisplayName => JobType;

    Task HandleAsync(JobExecutionContext context, CancellationToken ct);
}

public sealed class JobExecutionContext
{
    public required long JobId { get; init; }
    public required string JobType { get; init; }
    public required int Attempt { get; init; }
    public int? ShopId { get; init; }
    public string? PayloadJson { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Set by the handler: a short outcome for the Jobs page and notifications ("2 new, 5 updated").</summary>
    public string? Result { get; set; }

    public T? GetPayload<T>() =>
        PayloadJson is null ? default : JsonSerializer.Deserialize<T>(PayloadJson, JobJson.Options);
}

public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Throw from a handler to fail the job without further retries.</summary>
public sealed class JobPermanentFailureException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Unit used when parsing recurring job interval settings.</summary>
public enum IntervalUnit
{
    Seconds,
    Hours,
}

/// <summary>A job enqueued on a fixed interval by the recurring scheduler.</summary>
public sealed record RecurringJobDefinition(string Name, string JobType, TimeSpan Interval)
{
    public object? Payload { get; init; }

    /// <summary>Delay after a fresh install before the first run.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Optional app setting that overrides <see cref="Interval"/>, so it can be changed in the UI
    /// without a restart. Values below <see cref="MinInterval"/> are raised to it.
    /// </summary>
    public string? IntervalSettingKey { get; init; }

    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);

    public IntervalUnit IntervalUnit { get; init; } = IntervalUnit.Seconds;

    /// <summary>The interval to use, given the current value of <see cref="IntervalSettingKey"/> (if any).</summary>
    public TimeSpan EffectiveInterval(string? settingValue)
    {
        if (!int.TryParse(settingValue, out var units) || units <= 0)
            return Interval;

        var unit = IntervalUnit;
        if (unit == IntervalUnit.Seconds && IntervalSettingKey is not null &&
            IntervalSettingKey.EndsWith("Hours", StringComparison.OrdinalIgnoreCase))
        {
            unit = IntervalUnit.Hours;
        }

        var duration = unit switch
        {
            IntervalUnit.Hours => TimeSpan.FromHours(units),
            _ => TimeSpan.FromSeconds(units),
        };

        return duration < MinInterval ? MinInterval : duration;
    }
}

/// <summary>Wakes the job processor when new work is enqueued.</summary>
public interface IJobSignal
{
    void Notify();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>A job was queued, started, finished, rescheduled, retried or cancelled.</summary>
public sealed record JobChanged(
    long JobId,
    string JobType,
    string DisplayName,
    int? ShopId,
    JobStatus Status,
    string? CreatedBy,
    string? Result,
    string? Error)
{
    /// <summary>Succeeded, failed for good, or cancelled (not a retry that is still pending).</summary>
    public bool IsFinal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}

/// <summary>
/// In-process feed of job changes, so open pages can refresh when background work finishes instead of polling.
/// </summary>
public interface IJobEvents
{
    void Publish(JobChanged change);

    /// <summary>
    /// Calls <paramref name="onChange"/> after matching changes, coalescing bursts into one call per
    /// <paramref name="debounce"/> window. Dispose to unsubscribe.
    /// </summary>
    IDisposable Subscribe(Func<JobChanged, bool> filter, Func<IReadOnlyList<JobChanged>, Task> onChange, TimeSpan? debounce = null);
}
