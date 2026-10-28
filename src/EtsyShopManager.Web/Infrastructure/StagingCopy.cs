using EtsyShopManager.Core.Domain;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>
/// The staging copy of the app (docs/STAGING.md): the "Staging" environment, running on a copy of the production
/// database so changes can be looked at before they are merged. It must never reach Etsy, whatever its configuration
/// or data say: every Etsy address points at a dead local port, and on every start the Etsy app keys are deleted and
/// the shops' tokens are blanked and marked as needing reconnection (so the pollers and sweeps leave them alone).
/// </summary>
public static class StagingCopy
{
    /// <summary>The discard port on this machine: nothing listens there, so every Etsy call fails at once.</summary>
    public const string DeadEtsyAddress = "http://127.0.0.1:9";
    public const string DeadUspsAddress = "http://127.0.0.1:9";

    public const string TokenError = "Staging copy: Etsy is switched off here.";

    /// <summary>Written next to the binaries by scripts/deploy-staging.ps1: the branch that is deployed.</summary>
    public const string BranchFileName = "STAGING_BRANCH.txt";

    /// <summary>The deployed branch, shown in the app bar; null when the file is missing.</summary>
    public static string? Branch { get; } = ReadBranch();

    /// <summary>Points every Etsy address at <see cref="DeadEtsyAddress"/>, overriding configuration.</summary>
    public static IServiceCollection AddStagingEtsyLockout(this IServiceCollection services) =>
        services.PostConfigure<EtsyOptions>(o =>
        {
            o.ApiBaseUrl = DeadEtsyAddress;
            o.AuthorizeUrl = $"{DeadEtsyAddress}/oauth/connect";
            o.TokenUrl = $"{DeadEtsyAddress}/v3/public/oauth/token";
        });

    /// <summary>Points every USPS address at <see cref="DeadUspsAddress"/>, overriding configuration.</summary>
    public static IServiceCollection AddStagingUspsLockout(this IServiceCollection services) =>
        services.PostConfigure<EtsyShopManager.Web.Infrastructure.Usps.UspsOptions>(o =>
        {
            o.ApiBaseUrl = DeadUspsAddress;
            o.OAuthUrl = $"{DeadUspsAddress}/oauth2/v3/token";
        });

    /// <summary>Deletes the Etsy/USPS app keys and blanks every shop token. Returns how many rows it changed.</summary>
    public static async Task<int> RemoveEtsySecretsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var keys = await db.Settings
            .Where(s => s.Key == SettingKeys.EtsyKeystring || s.Key == SettingKeys.EtsySharedSecret
                     || s.Key == SettingKeys.UspsConsumerKey || s.Key == SettingKeys.UspsConsumerSecret)
            .ExecuteDeleteAsync(ct);
        var tokens = await db.ShopTokens.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.EncryptedAccessToken, "")
            .SetProperty(t => t.EncryptedRefreshToken, "")
            .SetProperty(t => t.NeedsReauth, true)
            .SetProperty(t => t.LastError, TokenError), ct);
        return keys + tokens;
    }

    public static async Task RemoveEtsySecretsAsync(IServiceProvider services, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var changed = await RemoveEtsySecretsAsync(db);
        logger.LogWarning("Staging copy: Etsy is switched off (addresses point at {Address}; {Rows} key/token row(s) cleared).",
            DeadEtsyAddress, changed);
    }

    private static string? ReadBranch()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, BranchFileName);
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } branch ? branch : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
