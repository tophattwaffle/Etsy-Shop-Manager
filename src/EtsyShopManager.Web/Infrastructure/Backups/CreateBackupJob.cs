using EtsyShopManager.Core.Jobs;

namespace EtsyShopManager.Web.Infrastructure.Backups;

/// <summary>
/// Background job that creates a full restorable backup archive and prunes old backups.
/// </summary>
public sealed class CreateBackupJob(BackupService backupService) : IJobHandler
{
    public const string Type = "backups.create";

    public string JobType => Type;
    public string DisplayName => "Create backup";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var fileName = await backupService.CreateRestorableBackupAsync(user: "scheduler", ct: ct);
        context.Result = $"Created backup: {fileName}";
    }
}
