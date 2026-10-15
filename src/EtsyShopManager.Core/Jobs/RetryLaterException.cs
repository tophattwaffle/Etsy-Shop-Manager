namespace EtsyShopManager.Core.Jobs;

/// <summary>
/// Thrown when work cannot run right now but is not failing (e.g. Etsy rate limit).
/// The job processor reschedules the job after <see cref="Delay"/> without counting an attempt.
/// </summary>
public class RetryLaterException(string message, TimeSpan delay, Exception? inner = null) : Exception(message, inner)
{
    public TimeSpan Delay { get; } = delay;
}
