using System.Globalization;
using System.Text.Json;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ShopDefaultsEmpiricalTests
{
    private sealed class TrackingAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? User)> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityType, entityId, details, userName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ShopDefaultsService_Saving_and_Loading_All_Fields_Set()
    {
        var settings = new InMemorySettings();
        var audit = new TrackingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        var allSet = new ShopDefaults(
            ShippingProfileId: 1001L,
            ReadinessStateId: 2001L,
            ReturnPolicyId: 3001L,
            ShopSectionId: 4001L,
            WhoMade: "someone_else",
            WhenMade: "2020_2026",
            TaxonomyId: 5001L);

        await service.SaveAsync(42, allSet, "alice");

        var loaded = await service.GetAsync(42);
        Assert.NotNull(loaded);
        Assert.Equal(1001L, loaded.ShippingProfileId);
        Assert.Equal(2001L, loaded.ReadinessStateId);
        Assert.Equal(3001L, loaded.ReturnPolicyId);
        Assert.Equal(4001L, loaded.ShopSectionId);
        Assert.Equal("someone_else", loaded.WhoMade);
        Assert.Equal("2020_2026", loaded.WhenMade);
        Assert.Equal(5001L, loaded.TaxonomyId);

        // Audit check
        Assert.Single(audit.Entries);
        var entry = audit.Entries[0];
        Assert.Equal("shop.defaults-updated", entry.Action);
        Assert.Equal("Shop", entry.EntityType);
        Assert.Equal("42", entry.EntityId);
        Assert.Equal("alice", entry.User);
        Assert.NotNull(entry.Details);

        var auditedObj = JsonSerializer.Deserialize<ShopDefaults>(entry.Details);
        Assert.NotNull(auditedObj);
        Assert.Equal(allSet, auditedObj);
    }

    [Fact]
    public async Task ShopDefaultsService_Saving_and_Loading_Partial_Nulls()
    {
        var settings = new InMemorySettings();
        var audit = new TrackingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        var partial = new ShopDefaults(
            ShippingProfileId: 1002L,
            ReadinessStateId: null,
            ReturnPolicyId: 3002L,
            ShopSectionId: null,
            WhoMade: null,
            WhenMade: "made_to_order",
            TaxonomyId: null);

        await service.SaveAsync(42, partial, "bob");

        var loaded = await service.GetAsync(42);
        Assert.NotNull(loaded);
        Assert.Equal(1002L, loaded.ShippingProfileId);
        Assert.Null(loaded.ReadinessStateId);
        Assert.Equal(3002L, loaded.ReturnPolicyId);
        Assert.Null(loaded.ShopSectionId);
        Assert.Null(loaded.WhoMade);
        Assert.Equal("made_to_order", loaded.WhenMade);
        Assert.Null(loaded.TaxonomyId);
    }

    [Fact]
    public async Task ShopDefaultsService_Saving_and_Loading_All_Nulls()
    {
        var settings = new InMemorySettings();
        var audit = new TrackingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        var allNull = new ShopDefaults(null, null, null, null, null, null, null);

        await service.SaveAsync(99, allNull, "system");

        var loaded = await service.GetAsync(99);
        Assert.NotNull(loaded);
        Assert.Null(loaded.ShippingProfileId);
        Assert.Null(loaded.ReadinessStateId);
        Assert.Null(loaded.ReturnPolicyId);
        Assert.Null(loaded.ShopSectionId);
        Assert.Null(loaded.WhoMade);
        Assert.Null(loaded.WhenMade);
        Assert.Null(loaded.TaxonomyId);
    }

    [Fact]
    public async Task ShopDefaultsService_Updating_Existing_Defaults_Emits_Audit_Log_Each_Time()
    {
        var settings = new InMemorySettings();
        var audit = new TrackingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        // First save
        var first = new ShopDefaults(100L, 200L, 300L, 400L, "i_did", "made_to_order", 500L);
        await service.SaveAsync(1, first, "admin1");

        var loaded1 = await service.GetAsync(1);
        Assert.Equal(first, loaded1);
        Assert.Single(audit.Entries);
        Assert.Equal("admin1", audit.Entries[0].User);

        // Update with partial changes
        var second = new ShopDefaults(101L, null, 301L, null, "collective", "2020_2026", null);
        await service.SaveAsync(1, second, "admin2");

        var loaded2 = await service.GetAsync(1);
        Assert.Equal(second, loaded2);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("shop.defaults-updated", audit.Entries[1].Action);
        Assert.Equal("admin2", audit.Entries[1].User);
        Assert.Equal("1", audit.Entries[1].EntityId);

        // Update with all nulls
        var third = new ShopDefaults(null, null, null, null, null, null, null);
        await service.SaveAsync(1, third, "admin3");

        var loaded3 = await service.GetAsync(1);
        Assert.Equal(third, loaded3);
        Assert.Equal(3, audit.Entries.Count);
        Assert.Equal("admin3", audit.Entries[2].User);
    }

    [Fact]
    public async Task ListingCloner_DefaultsAsync_Target_Shop_Adopts_Configured_Defaults()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Source Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        const int targetShopId = 2;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.Shops.Add(new Shop
            {
                Id = targetShopId,
                EtsyShopId = 888L,
                EtsyUserId = 999L,
                Name = "TargetShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = shop.Time.Now.UtcDateTime,
                Token = new ShopToken { EncryptedAccessToken = "t", EncryptedRefreshToken = "r", Scopes = "listings_w" }
            });
            await db.SaveChangesAsync();
        }

        var settings = shop.Provider.GetRequiredService<ISettingsStore>();
        var audit = new TrackingAuditLog();
        var defaultsService = new ShopDefaultsService(settings, audit);
        var cloner = new ListingCloner(shop.Db, shop.Provider.GetRequiredService<ImageStore>(),
            shop.Provider.GetRequiredService<RecordingJobQueue>(), settings, audit, shop.Time, defaultsService);

        int sourceListingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            sourceListingId = (await db.Listings.FirstAsync()).Id;
        }

        // Configure full defaults for target shop
        var defaults = new ShopDefaults(
            ShippingProfileId: 111L,
            ReadinessStateId: 222L,
            ReturnPolicyId: 333L,
            ShopSectionId: 444L);
        await defaultsService.SaveAsync(targetShopId, defaults, "tester");

        var cloneSettings = await cloner.DefaultsAsync(sourceListingId, targetShopId);

        Assert.Equal(targetShopId, cloneSettings.TargetShopId);
        Assert.Equal(111L, cloneSettings.ShippingProfileId);
        Assert.Equal(222L, cloneSettings.ReadinessStateId);
        Assert.Equal(333L, cloneSettings.ReturnPolicyId);
        Assert.Equal(444L, cloneSettings.ShopSectionId);
    }

    [Fact]
    public async Task ListingCloner_DefaultsAsync_Target_Shop_Falls_Back_To_LastChoice_When_No_Defaults()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Source Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        const int targetShopId = 2;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.Shops.Add(new Shop
            {
                Id = targetShopId,
                EtsyShopId = 888L,
                EtsyUserId = 999L,
                Name = "TargetShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = shop.Time.Now.UtcDateTime,
                Token = new ShopToken { EncryptedAccessToken = "t", EncryptedRefreshToken = "r", Scopes = "listings_w" }
            });
            await db.SaveChangesAsync();
        }

        var settings = shop.Provider.GetRequiredService<ISettingsStore>();
        var audit = new TrackingAuditLog();
        var defaultsService = new ShopDefaultsService(settings, audit);
        var cloner = new ListingCloner(shop.Db, shop.Provider.GetRequiredService<ImageStore>(),
            shop.Provider.GetRequiredService<RecordingJobQueue>(), settings, audit, shop.Time, defaultsService);

        int sourceListingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            sourceListingId = (await db.Listings.FirstAsync()).Id;
        }

        // Set LastChoice in settings, but NO ShopDefaults
        var lastChoice = new CloneSettings(targetShopId, "", 555L, 666L, 777L, 888L);
        await settings.SetAsync(ListingCloner.LastChoiceKey(targetShopId), JsonSerializer.Serialize(lastChoice));

        var cloneSettings = await cloner.DefaultsAsync(sourceListingId, targetShopId);

        Assert.Equal(targetShopId, cloneSettings.TargetShopId);
        Assert.Equal(555L, cloneSettings.ShippingProfileId);
        Assert.Equal(666L, cloneSettings.ReadinessStateId);
        Assert.Equal(777L, cloneSettings.ReturnPolicyId);
        Assert.Equal(888L, cloneSettings.ShopSectionId);
    }

    [Fact]
    public async Task ListingCloner_DefaultsAsync_Target_Shop_Defaults_Override_LastChoice_Per_Field()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Source Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        const int targetShopId = 2;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.Shops.Add(new Shop
            {
                Id = targetShopId,
                EtsyShopId = 888L,
                EtsyUserId = 999L,
                Name = "TargetShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = shop.Time.Now.UtcDateTime,
                Token = new ShopToken { EncryptedAccessToken = "t", EncryptedRefreshToken = "r", Scopes = "listings_w" }
            });
            await db.SaveChangesAsync();
        }

        var settings = shop.Provider.GetRequiredService<ISettingsStore>();
        var audit = new TrackingAuditLog();
        var defaultsService = new ShopDefaultsService(settings, audit);
        var cloner = new ListingCloner(shop.Db, shop.Provider.GetRequiredService<ImageStore>(),
            shop.Provider.GetRequiredService<RecordingJobQueue>(), settings, audit, shop.Time, defaultsService);

        int sourceListingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            sourceListingId = (await db.Listings.FirstAsync()).Id;
        }

        // Defaults only has ShippingProfileId and ReturnPolicyId set. ReadinessStateId and ShopSectionId are null.
        var partialDefaults = new ShopDefaults(
            ShippingProfileId: 100L,
            ReadinessStateId: null,
            ReturnPolicyId: 300L,
            ShopSectionId: null);
        await defaultsService.SaveAsync(targetShopId, partialDefaults, "tester");

        // LastChoice has all fields set
        var lastChoice = new CloneSettings(targetShopId, "", 999L, 200L, 999L, 400L);
        await settings.SetAsync(ListingCloner.LastChoiceKey(targetShopId), JsonSerializer.Serialize(lastChoice));

        var cloneSettings = await cloner.DefaultsAsync(sourceListingId, targetShopId);

        // ShippingProfileId: 100L from defaults
        Assert.Equal(100L, cloneSettings.ShippingProfileId);
        // ReadinessStateId: 200L from lastChoice
        Assert.Equal(200L, cloneSettings.ReadinessStateId);
        // ReturnPolicyId: 300L from defaults
        Assert.Equal(300L, cloneSettings.ReturnPolicyId);
        // ShopSectionId: 400L from lastChoice
        Assert.Equal(400L, cloneSettings.ShopSectionId);
    }

    [Fact]
    public async Task ListingCloner_DefaultsAsync_Same_Shop_Preserves_Source_Listing_Profiles()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Same Shop Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var settings = shop.Provider.GetRequiredService<ISettingsStore>();
        var audit = new TrackingAuditLog();
        var defaultsService = new ShopDefaultsService(settings, audit);
        var cloner = new ListingCloner(shop.Db, shop.Provider.GetRequiredService<ImageStore>(),
            shop.Provider.GetRequiredService<RecordingJobQueue>(), settings, audit, shop.Time, defaultsService);

        // Set shop defaults for the same shop with completely different values
        await defaultsService.SaveAsync(FakeEtsyShop.ShopId, new ShopDefaults(999L, 999L, 999L, 999L), "tester");

        int sourceListingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            sourceListingId = (await db.Listings.FirstAsync()).Id;
        }

        // Cloning into SAME shop (FakeEtsyShop.ShopId)
        var cloneSettings = await cloner.DefaultsAsync(sourceListingId, FakeEtsyShop.ShopId);

        // Must preserve source listing profiles (501, 601, 701, 801), NOT shop defaults (999, 999, 999, 999)
        Assert.Equal(FakeEtsyShop.ShopId, cloneSettings.TargetShopId);
        Assert.Equal(501L, cloneSettings.ShippingProfileId);
        Assert.Equal(601L, cloneSettings.ReadinessStateId);
        Assert.Equal(701L, cloneSettings.ReturnPolicyId);
        Assert.Equal(801L, cloneSettings.ShopSectionId);
    }
}
