using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Infrastructure.Backups;
using EtsyShopManager.Web.Infrastructure.Jobs;
using Microsoft.AspNetCore.Authorization;
using MudBlazor;

namespace EtsyShopManager.Web.Modules;

/// <summary>Users, settings, audit log, backups and exports.</summary>
public sealed class AdminModule : IModule
{
    public string Name => "Admin";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<BackupService>();
        services.AddJobHandler<CreateBackupJob>();
        services.AddRecurringJob(new RecurringJobDefinition("Scheduled backup", CreateBackupJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromHours(1),
            IntervalSettingKey = SettingKeys.BackupsScheduleIntervalHours,
            MinInterval = TimeSpan.FromHours(1),
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/backups")
            .RequireAuthorization(new AuthorizeAttribute { Roles = Roles.Admin });

        group.MapGet("/download/{fileName}", (string fileName, BackupService backups) =>
        {
            try
            {
                var path = backups.GetBackupPath(fileName);
                return Results.File(path, "application/zip", fileName);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest("Invalid file name.");
            }
        });

        group.MapGet("/export", async (BackupService backups, CancellationToken ct) =>
        {
            var fileName = await backups.CreateListingExportAsync(user: "Admin", ct: ct);
            var path = backups.GetBackupPath(fileName);
            return Results.File(path, "application/zip", fileName);
        });
    }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Users", "admin/users", Icons.Material.Filled.People, Order: 910, Roles: Roles.Admin, Group: NavGroups.Settings),
        new("Audit log", "admin/audit", Icons.Material.Filled.History, Order: 920, Roles: Roles.Admin, Group: NavGroups.Settings),
        new("Etsy writes", "admin/etsy-writes", Icons.Material.Filled.CloudUpload, Order: 925, Roles: Roles.Admin, Group: NavGroups.Settings),
        new("General", "admin/settings", Icons.Material.Filled.Tune, Order: 899, Roles: Roles.Admin, Group: NavGroups.Settings),
    ];
}
