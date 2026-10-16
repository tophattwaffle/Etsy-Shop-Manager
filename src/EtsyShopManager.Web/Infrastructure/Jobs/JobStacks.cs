using EtsyShopManager.Core.Jobs;

namespace EtsyShopManager.Web.Infrastructure.Jobs;

/// <summary>Consecutive jobs of one type (newest first), shown as one expandable row on the Jobs page.</summary>
public sealed record JobStack(IReadOnlyList<Job> Jobs)
{
    public Job Latest => Jobs[0];
    public string JobType => Latest.JobType;
    public int Count => Jobs.Count;

    /// <summary>Stable while newer jobs join the top of the stack, so an expanded stack stays expanded.</summary>
    public long Key => Jobs[^1].Id;
}

public static class JobStacks
{
    /// <summary>
    /// Groups a newest-first job list into runs of the same type. A different type ends the run, and a failed job is
    /// always its own row, so a failure is never hidden inside a collapsed stack.
    /// </summary>
    public static List<JobStack> Group(IEnumerable<Job> newestFirst)
    {
        var stacks = new List<JobStack>();
        List<Job>? current = null;
        foreach (var job in newestFirst)
        {
            var joins = current is not null && job.Status != JobStatus.Failed
                        && current[0].JobType == job.JobType && current[^1].Status != JobStatus.Failed;
            if (!joins)
            {
                current = [];
                stacks.Add(new JobStack(current));
            }
            current!.Add(job);
        }
        return stacks;
    }
}
