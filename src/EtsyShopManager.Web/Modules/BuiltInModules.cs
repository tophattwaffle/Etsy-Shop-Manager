using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Jobs;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MudBlazor;

namespace EtsyShopManager.Web.Modules;

/// <summary>Shop connections: Etsy OAuth endpoints, shop info refresh, token keep-alive.</summary>
public sealed class ShopsModule : IModule
{
    public string Name => "Shops";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ShopService>();
        services.AddJobHandler<TokenKeepAliveJob>();
        services.AddJobHandler<ShopRefreshJob>();
        services.AddJobHandler<RefreshAllShopsJob>();
        services.AddScoped<IDomainEventHandler<ShopNeedsReauth>, AuditShopNeedsReauth>();

        services.AddRecurringJob(new RecurringJobDefinition("Etsy token keep-alive", TokenKeepAliveJob.Type, TimeSpan.FromHours(12)));
        services.AddRecurringJob(new RecurringJobDefinition("Refresh shop details", RefreshAllShopsJob.Type, TimeSpan.FromHours(24))
        {
            InitialDelay = TimeSpan.FromMinutes(5),
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => EtsyAuthEndpoints.Map(endpoints);

    public IEnumerable<NavItem> NavItems =>
    [
        new("Shops", "shops", Icons.Material.Filled.Storefront, Order: 10),
    ];
}

/// <summary>Background job monitoring and housekeeping.</summary>
public sealed class JobsModule : IModule
{
    public string Name => "Jobs";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobHandler<JobsCleanupJob>();
        // Every 6 h, so routine scheduled jobs (kept 24 h) don't pile up between runs.
        services.AddRecurringJob(new RecurringJobDefinition("Clean up old jobs", JobsCleanupJob.Type, TimeSpan.FromHours(6))
        {
            InitialDelay = TimeSpan.FromMinutes(10),
        });
    }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Jobs", "jobs", Icons.Material.Filled.Queue, Order: 900, Roles: Roles.Admin),
    ];
}

/// <summary>Users, settings, audit log.</summary>
public sealed class AdminModule : IModule
{
    public string Name => "Admin";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Users", "admin/users", Icons.Material.Filled.People, Order: 910, Roles: Roles.Admin),
        new("Audit log", "admin/audit", Icons.Material.Filled.History, Order: 920, Roles: Roles.Admin),
        new("Settings", "admin/settings", Icons.Material.Filled.Settings, Order: 930, Roles: Roles.Admin),
    ];
}

/// <summary>Deletes finished jobs past their retention period.</summary>
public sealed class JobsCleanupJob(IDbContextFactory<AppDbContext> dbFactory, IOptions<JobOptions> options, TimeProvider time)
    : IJobHandler
{
    public const string Type = "jobs.cleanup";
    public string JobType => Type;
    public string DisplayName => "Clean up old jobs";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var okCutoff = now.AddDays(-options.Value.SucceededRetentionDays);
        var failedCutoff = now.AddDays(-options.Value.FailedRetentionDays);

        await db.Jobs.Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Cancelled) && j.CompletedAtUtc < okCutoff)
            .ExecuteDeleteAsync(ct);
        var scheduledCutoff = now.AddHours(-options.Value.ScheduledSucceededRetentionHours);
        await db.Jobs.Where(j => j.Status == JobStatus.Succeeded && j.CreatedBy == "scheduler" && j.CompletedAtUtc < scheduledCutoff)
            .ExecuteDeleteAsync(ct);
        await db.Jobs.Where(j => j.Status == JobStatus.Failed && j.CompletedAtUtc < failedCutoff)
            .ExecuteDeleteAsync(ct);
    }
}
