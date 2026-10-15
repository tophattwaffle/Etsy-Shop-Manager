using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>Tracks first-run setup: whether an Admin exists, and whether the wizard was finished.</summary>
public sealed class SetupState(IDbContextFactory<AppDbContext> dbFactory, ISettingsStore settings, TimeProvider time)
{
    private volatile bool _adminExists;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> AdminExistsAsync(CancellationToken ct = default)
    {
        if (_adminExists) return true;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await (from ur in db.UserRoles
                            join r in db.Roles on ur.RoleId equals r.Id
                            where r.Name == Roles.Admin
                            select ur).AnyAsync(ct);
        if (exists) _adminExists = true;
        return exists;
    }

    public async Task<bool> IsSetupCompletedAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.SetupCompleted, ct) == "true";

    public Task MarkSetupCompletedAsync(string? userName, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.SetupCompleted, "true", updatedBy: userName, ct: ct);

    /// <summary>Creates the first Admin. Fails if an Admin already exists (checked under a lock).</summary>
    public async Task<(IdentityResult Result, AppUser? User)> CreateFirstAdminAsync(
        UserManager<AppUser> userManager, string userName, string? displayName, string password)
    {
        await _gate.WaitAsync();
        try
        {
            if (await AdminExistsAsync())
                return (IdentityResult.Failed(new IdentityError { Code = "AdminExists", Description = "An Admin account already exists." }), null);

            var user = new AppUser
            {
                UserName = userName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded) return (result, null);

            result = await userManager.AddToRoleAsync(user, Roles.Admin);
            if (!result.Succeeded)
            {
                await userManager.DeleteAsync(user);
                return (result, null);
            }

            _adminExists = true;
            return (result, user);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Until an Admin exists, every page request is sent to the first-run wizard.</summary>
public sealed class SetupGateMiddleware(RequestDelegate next, SetupState setup)
{
    private static readonly string[] AllowedPrefixes =
        ["/setup", "/_framework", "/_content", "/_blazor", "/health", "/not-found", "/Error"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || Path.HasExtension(path)
            || await setup.AdminExistsAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        context.Response.Redirect($"{context.Request.PathBase}/setup");
    }
}
