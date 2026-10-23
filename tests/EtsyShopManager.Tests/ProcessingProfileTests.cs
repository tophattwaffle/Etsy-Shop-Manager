using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ProcessingProfileTests
{
    private sealed class TestAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? User)> Entries { get; } = [];
        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityType, entityId, details, userName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Create_processing_profile_calls_Etsy_api_and_saves_to_database_and_audit_log()
    {
        using var shop = new FakeEtsyShop();
        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        var model = new CreateProcessingProfileModel
        {
            ReadinessState = "made_to_order",
            MinProcessingTime = 2,
            MaxProcessingTime = 5,
            ProcessingTimeUnit = "days"
        };

        var profile = await service.CreateAsync(FakeEtsyShop.ShopId, model, "tophattwaffle", CancellationToken.None);

        Assert.NotNull(profile);
        Assert.True(profile.ReadinessStateId > 0);
        Assert.Equal("made_to_order", profile.ReadinessState);
        Assert.Equal(2, profile.MinProcessingDays);
        Assert.Equal(5, profile.MaxProcessingDays);

        // Verify Etsy write recorded
        var write = Assert.Single(shop.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions", write.Path);
        Assert.Contains("readiness_state=made_to_order", write.Body);
        Assert.Contains("min_processing_time=2", write.Body);
        Assert.Contains("max_processing_time=5", write.Body);

        // Verify database persistence
        await using var db = shop.Db.CreateDbContext();
        var stored = await db.ProcessingProfiles.SingleOrDefaultAsync(p => p.ReadinessStateId == profile.ReadinessStateId);
        Assert.NotNull(stored);
        Assert.Equal(FakeEtsyShop.ShopId, stored.ShopId);
        Assert.Equal(2, stored.MinProcessingDays);
        Assert.Equal(5, stored.MaxProcessingDays);

        // Verify audit log
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("processing-profile.created", entry.Action);
        Assert.Equal(profile.ReadinessStateId.ToString(), entry.EntityId);
        Assert.Equal("tophattwaffle", entry.User);
    }

    [Fact]
    public async Task Create_processing_profile_validates_inputs_before_calling_Etsy()
    {
        using var shop = new FakeEtsyShop();
        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // Min > Max
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(FakeEtsyShop.ShopId, new CreateProcessingProfileModel("made_to_order", 5, 2), "tophattwaffle"));

        // Min < 1
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(FakeEtsyShop.ShopId, new CreateProcessingProfileModel("made_to_order", 0, 2), "tophattwaffle"));

        // Invalid readiness state
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(FakeEtsyShop.ShopId, new CreateProcessingProfileModel("invalid_state", 1, 2), "tophattwaffle"));

        Assert.Empty(shop.Writes);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Update_processing_profile_calls_Etsy_api_and_updates_database_and_audit_log()
    {
        using var shop = new FakeEtsyShop();
        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        await using (var db = shop.Db.CreateDbContext())
        {
            db.ProcessingProfiles.Add(new ProcessingProfile
            {
                ShopId = FakeEtsyShop.ShopId,
                ReadinessStateId = 601,
                ReadinessState = "made_to_order",
                MinProcessingDays = 1,
                MaxProcessingDays = 3,
                DisplayLabel = "1-3 days"
            });
            await db.SaveChangesAsync();
        }

        var updateModel = new UpdateProcessingProfileModel
        {
            ReadinessState = "ready_to_ship",
            MinProcessingTime = 1,
            MaxProcessingTime = 1,
            ProcessingTimeUnit = "days"
        };

        var updated = await service.UpdateAsync(FakeEtsyShop.ShopId, 601, updateModel, "tophattwaffle", CancellationToken.None);

        Assert.Equal("ready_to_ship", updated.ReadinessState);
        Assert.Equal(1, updated.MinProcessingDays);
        Assert.Equal(1, updated.MaxProcessingDays);

        var write = Assert.Single(shop.Writes);
        Assert.Equal("PUT", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions/601", write.Path);
        Assert.Contains("readiness_state=ready_to_ship", write.Body);

        await using var dbCheck = shop.Db.CreateDbContext();
        var stored = await dbCheck.ProcessingProfiles.SingleAsync(p => p.ReadinessStateId == 601);
        Assert.Equal("ready_to_ship", stored.ReadinessState);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("processing-profile.updated", entry.Action);
        Assert.Equal("601", entry.EntityId);
    }

    [Fact]
    public async Task Update_processing_profile_fails_when_profile_belongs_to_different_shop()
    {
        using var shop = new FakeEtsyShop();
        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        await using (var db = shop.Db.CreateDbContext())
        {
            // Belongs to shop 2, not FakeEtsyShop.ShopId
            db.Shops.Add(new Shop
            {
                Id = 2,
                EtsyShopId = 43,
                EtsyUserId = 222,
                Name = "SecondShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow,
                Token = new ShopToken { EncryptedAccessToken = "tok_access", EncryptedRefreshToken = "tok_refresh", Scopes = "listings_w" },
            });
            db.ProcessingProfiles.Add(new ProcessingProfile
            {
                ShopId = 2,
                ReadinessStateId = 699,
                ReadinessState = "made_to_order",
                MinProcessingDays = 1,
                MaxProcessingDays = 3,
            });
            await db.SaveChangesAsync();
        }

        var updateModel = new UpdateProcessingProfileModel
        {
            ReadinessState = "ready_to_ship",
            MinProcessingTime = 1,
            MaxProcessingTime = 2,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(FakeEtsyShop.ShopId, 699, updateModel, "tophattwaffle"));

        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task List_processing_profiles_calculates_correct_usage_counts_across_listings()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Vase A"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Vase B"));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "Vase C"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 602, ReadinessState = "ready_to_ship", MinProcessingDays = 1, MaxProcessingDays = 1, DisplayLabel = "1 day" });

            // Assign listing 1 & 2 to 601, listing 3 to 602
            var l1 = await db.Listings.Include(l => l.Products).ThenInclude(p => p.Offerings).SingleAsync(l => l.EtsyListingId == 1);
            var l2 = await db.Listings.Include(l => l.Products).ThenInclude(p => p.Offerings).SingleAsync(l => l.EtsyListingId == 2);
            var l3 = await db.Listings.Include(l => l.Products).ThenInclude(p => p.Offerings).SingleAsync(l => l.EtsyListingId == 3);

            l1.ReadinessStateId = 601;
            foreach (var o in l1.Products.SelectMany(p => p.Offerings)) o.ReadinessStateId = 601;

            l2.ReadinessStateId = 601;
            foreach (var o in l2.Products.SelectMany(p => p.Offerings)) o.ReadinessStateId = 601;

            l3.ReadinessStateId = 602;
            foreach (var o in l3.Products.SelectMany(p => p.Offerings)) o.ReadinessStateId = 602;

            await db.SaveChangesAsync();
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        var profiles = await service.ListAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.Equal(2, profiles.Count);
        var p601 = profiles.Single(p => p.ReadinessStateId == 601);
        var p602 = profiles.Single(p => p.ReadinessStateId == 602);

        Assert.Equal(2, p601.ListingCount);
        Assert.Equal(1, p602.ListingCount);
    }

    [Fact]
    public async Task Bulk_apply_previews_changes_without_saving_or_sending_writes()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Vase 1"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int id1;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 602, ReadinessState = "ready_to_ship", MinProcessingDays = 1, MaxProcessingDays = 1, DisplayLabel = "1 day" });
            id1 = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
            await db.SaveChangesAsync();
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        var preview = await service.PreviewApplyAsync(FakeEtsyShop.ShopId, 602, [id1], CancellationToken.None);
        var row = Assert.Single(preview);
        Assert.True(row.Changes);
        Assert.Equal("1 day", row.After);

        // Asserts zero draft changes saved
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.SingleAsync(l => l.Id == id1);
            Assert.False(l.HasDraft);
        }
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Bulk_apply_saves_unpushed_draft_inventory_edits_and_skips_drifted_or_missing()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 day",
        });
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Clean Vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Drifted Vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "Missing Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int id1, id2, id3;
        await using (var db = shop.Db.CreateDbContext())
        {
            id1 = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
            var l2 = await db.Listings.SingleAsync(l => l.EtsyListingId == 2);
            id2 = l2.Id;
            var l3 = await db.Listings.SingleAsync(l => l.EtsyListingId == 3);
            id3 = l3.Id;
        }

        // Cause inventory drift on listing 2
        shop.ListingJson(2)["inventory"]!["products"]![0]!["offerings"]![0]!["price"]!["amount"] = 9999;
        shop.ListingJson(2)["last_modified_timestamp"] = 1_760_000_900;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var l3 = await db.Listings.SingleAsync(l => l.Id == id3);
            l3.IsMissingOnEtsy = true;
            await db.SaveChangesAsync();
        }

        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // Preview
        var preview = await service.PreviewApplyAsync(FakeEtsyShop.ShopId, 602, [id1, id2, id3], CancellationToken.None);
        Assert.Equal(3, preview.Count);
        Assert.True(preview.Single(r => r.ListingId == id1).Changes);
        Assert.Contains("changed on Etsy", preview.Single(r => r.ListingId == id2).Skipped);
        Assert.Contains("Gone from Etsy", preview.Single(r => r.ListingId == id3).Skipped);

        // Apply
        var changed = await service.ApplyAsync(FakeEtsyShop.ShopId, 602, [id1, id2, id3], "tophattwaffle", CancellationToken.None);
        Assert.Equal([id1], changed);

        // Assert listing 1 has draft inventory with readiness_state_id = 602
        await using (var db = shop.Db.CreateDbContext())
        {
            var l1 = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == id1);
            Assert.True(l1.HasDraft);
            var model = ListingEditModel.Parse(l1.EtsyState!.ManagedJson, l1.EtsyState.BaseJson, l1.DraftJson);
            Assert.Contains(ListingFields.Inventory, model.PendingFields);

            var offerings = (model.Local(ListingFields.Inventory)!["products"]?.AsArray() ?? [])
                .SelectMany(p => p!["offerings"]?.AsArray() ?? []);
            Assert.All(offerings, o => Assert.Equal(602, (long?)o!["readiness_state_id"]));
        }

        // Zero API writes and zero jobs queued
        Assert.Empty(shop.Writes);
        Assert.Empty(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
    }

    [Fact]
    public async Task Bulk_apply_skips_listings_already_using_target_profile()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Vase");
        foreach (var p in listing["inventory"]!["products"]!.AsArray())
            foreach (var o in p!["offerings"]!.AsArray())
                o!["readiness_state_id"] = 601L;
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int id1;
        await using (var db = shop.Db.CreateDbContext())
        {
            id1 = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        // Both offerings already have readiness_state_id = 601 (from FakeEtsyShop.Listing default)
        var preview = await service.PreviewApplyAsync(FakeEtsyShop.ShopId, 601, [id1], CancellationToken.None);
        var row = Assert.Single(preview);
        Assert.False(row.Changes);
        Assert.Equal("Already uses this profile.", row.Skipped);

        var changed = await service.ApplyAsync(FakeEtsyShop.ShopId, 601, [id1], "tophattwaffle", CancellationToken.None);
        Assert.Empty(changed);
    }

    [Fact]
    public void Bulk_apply_filters_by_text_search_title_sku_tag_and_listing_id()
    {
        var item = new ProcessingProfileCandidate(
            ListingId: 1,
            Title: "Ceramic Planter",
            ShopId: 42,
            ShopName: "ClayWorks",
            EtsyListingId: 123456789,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["PLANTER-SM", "PLANTER-LG"],
            Tags: ["plant pot", "gardening", "handmade"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "1-3 days"
        );

        // Title match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "planter" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ceramic" }));

        // SKU match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "PLANTER-SM" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "-lg" }));

        // Tag match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "gardening" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "pot" }));

        // Listing ID match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "123456789" }));

        // Non-match
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "metal" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "999999" }));
    }

    [Fact]
    public void Bulk_apply_filters_by_status_active_draft_expired_sold_out_inactive_and_missing()
    {
        var active = new ProcessingProfileCandidate(1, "A", 42, "S", 101, "active", false, [], [], null, "—");
        var draft = new ProcessingProfileCandidate(2, "B", 42, "S", 102, "draft", false, [], [], null, "—");
        var expired = new ProcessingProfileCandidate(3, "C", 42, "S", 103, "expired", false, [], [], null, "—");
        var soldOut = new ProcessingProfileCandidate(4, "D", 42, "S", 104, "sold_out", false, [], [], null, "—");
        var inactive = new ProcessingProfileCandidate(5, "E", 42, "S", 105, "inactive", false, [], [], null, "—");
        var missing = new ProcessingProfileCandidate(6, "F", 42, "S", 106, "active", true, [], [], null, "—");

        // "all" includes non-missing only
        Assert.True(TemplateFilterMatcher.Matches(active, new TemplateListingFilter { Status = "all" }));
        Assert.True(TemplateFilterMatcher.Matches(draft, new TemplateListingFilter { Status = "all" }));
        Assert.False(TemplateFilterMatcher.Matches(missing, new TemplateListingFilter { Status = "all" }));

        // Status specific filters
        Assert.True(TemplateFilterMatcher.Matches(active, new TemplateListingFilter { Status = "active" }));
        Assert.False(TemplateFilterMatcher.Matches(draft, new TemplateListingFilter { Status = "active" }));

        Assert.True(TemplateFilterMatcher.Matches(draft, new TemplateListingFilter { Status = "draft" }));
        Assert.True(TemplateFilterMatcher.Matches(expired, new TemplateListingFilter { Status = "expired" }));
        Assert.True(TemplateFilterMatcher.Matches(soldOut, new TemplateListingFilter { Status = "sold_out" }));
        Assert.True(TemplateFilterMatcher.Matches(inactive, new TemplateListingFilter { Status = "inactive" }));

        // "missing" status matches missing listings
        Assert.True(TemplateFilterMatcher.Matches(missing, new TemplateListingFilter { Status = "missing" }));
        Assert.False(TemplateFilterMatcher.Matches(active, new TemplateListingFilter { Status = "missing" }));
    }
}
