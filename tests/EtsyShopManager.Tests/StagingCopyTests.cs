using EtsyShopManager.Core.Domain;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

/// <summary>The staging copy runs on production data and must never reach Etsy (docs/STAGING.md).</summary>
public class StagingCopyTests
{
    [Fact]
    public void Etsy_addresses_point_at_a_dead_port_whatever_the_configuration_says()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Etsy:ApiBaseUrl"] = "https://api.etsy.com",
            ["Etsy:AuthorizeUrl"] = "https://www.etsy.com/oauth/connect",
            ["Etsy:TokenUrl"] = "https://api.etsy.com/v3/public/oauth/token",
        }).Build();
        var services = new ServiceCollection();
        services.AddEtsy(config);
        services.AddStagingEtsyLockout();

        var o = services.BuildServiceProvider().GetRequiredService<IOptions<EtsyOptions>>().Value;

        foreach (var url in new[] { o.ApiBaseUrl, o.AuthorizeUrl, o.TokenUrl })
        {
            Assert.StartsWith(StagingCopy.DeadEtsyAddress, url);
            Assert.DoesNotContain("etsy", new Uri(url).Host, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Usps_addresses_point_at_a_dead_port_in_staging()
    {
        var services = new ServiceCollection();
        services.Configure<EtsyShopManager.Web.Infrastructure.Usps.UspsOptions>(o =>
        {
            o.ApiBaseUrl = "https://api.usps.com";
            o.OAuthUrl = "https://api.usps.com/oauth2/v3/token";
        });
        services.AddStagingUspsLockout();

        var o = services.BuildServiceProvider().GetRequiredService<IOptions<EtsyShopManager.Web.Infrastructure.Usps.UspsOptions>>().Value;

        Assert.StartsWith(StagingCopy.DeadUspsAddress, o.ApiBaseUrl);
        Assert.StartsWith(StagingCopy.DeadUspsAddress, o.OAuthUrl);
        Assert.DoesNotContain("usps", new Uri(o.ApiBaseUrl).Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_removes_the_Etsy_and_Usps_keys_and_blanks_shop_tokens_but_keeps_everything_else()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            db.Shops.Add(new Shop
            {
                EtsyShopId = 1, Name = "One", ConnectedAtUtc = DateTime.UtcNow,
                Token = new ShopToken { EncryptedAccessToken = "access", EncryptedRefreshToken = "refresh", Scopes = "listings_w" },
            });
            db.Settings.AddRange(
                new AppSetting { Key = SettingKeys.EtsyKeystring, Value = "keystring" },
                new AppSetting { Key = SettingKeys.EtsySharedSecret, Value = "secret", IsEncrypted = true },
                new AppSetting { Key = SettingKeys.UspsConsumerKey, Value = "usps_key" },
                new AppSetting { Key = SettingKeys.UspsConsumerSecret, Value = "usps_secret", IsEncrypted = true },
                new AppSetting { Key = SettingKeys.SetupCompleted, Value = "true" });
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
            Assert.Equal(5, await StagingCopy.RemoveEtsySecretsAsync(db));

        await using (var db = testDb.CreateDbContext())
        {
            Assert.Equal([SettingKeys.SetupCompleted], await db.Settings.Select(s => s.Key).ToListAsync());
            var shop = await db.Shops.Include(s => s.Token).SingleAsync();
            Assert.Equal("One", shop.Name);
            Assert.True(shop.IsActive);
            Assert.Equal("", shop.Token!.EncryptedAccessToken);
            Assert.Equal("", shop.Token.EncryptedRefreshToken);
            Assert.True(shop.Token.NeedsReauth);
            Assert.Equal(StagingCopy.TokenError, shop.Token.LastError);
        }
    }
}
