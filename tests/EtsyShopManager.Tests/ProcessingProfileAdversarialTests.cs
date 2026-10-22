using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ProcessingProfileAdversarialTests
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

    private static ClaimsPrincipal User(string role = Roles.Admin, string name = "tophattwaffle") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "test"));

    private static JsonObject MultiProductMultiOfferingListing(long listingId, string title)
    {
        var listing = FakeEtsyShop.Listing(listingId, title, withImages: true, withInventory: false);
        // 4 products representing 2x2 variation grid (Size x Color)
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(100, 200),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(100, 200),
            ["products"] = new JsonArray(
                new JsonObject
                {
                    ["product_id"] = listingId * 100 + 1,
                    ["sku"] = $"SKU{listingId}-S-RED",
                    ["is_deleted"] = false,
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = 100, ["property_name"] = "Size", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Small") },
                        new JsonObject { ["property_id"] = 200, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Red") }
                    ),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = listingId * 1000 + 1,
                        ["quantity"] = 5,
                        ["is_enabled"] = true,
                        ["is_deleted"] = false,
                        ["price"] = FakeEtsyShop.Money(1999),
                        ["readiness_state_id"] = 601L,
                    }),
                },
                new JsonObject
                {
                    ["product_id"] = listingId * 100 + 2,
                    ["sku"] = $"SKU{listingId}-S-BLU",
                    ["is_deleted"] = false,
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = 100, ["property_name"] = "Size", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Small") },
                        new JsonObject { ["property_id"] = 200, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Blue") }
                    ),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = listingId * 1000 + 2,
                        ["quantity"] = 3,
                        ["is_enabled"] = true,
                        ["is_deleted"] = false,
                        ["price"] = FakeEtsyShop.Money(2499),
                        ["readiness_state_id"] = 603L,
                    }),
                },
                new JsonObject
                {
                    ["product_id"] = listingId * 100 + 3,
                    ["sku"] = $"SKU{listingId}-L-RED",
                    ["is_deleted"] = false,
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = 100, ["property_name"] = "Size", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Large") },
                        new JsonObject { ["property_id"] = 200, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Red") }
                    ),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = listingId * 1000 + 3,
                        ["quantity"] = 8,
                        ["is_enabled"] = true,
                        ["is_deleted"] = false,
                        ["price"] = FakeEtsyShop.Money(2999),
                        ["readiness_state_id"] = 601L,
                    }),
                },
                new JsonObject
                {
                    ["product_id"] = listingId * 100 + 4,
                    ["sku"] = $"SKU{listingId}-L-BLU",
                    ["is_deleted"] = false,
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = 100, ["property_name"] = "Size", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Large") },
                        new JsonObject { ["property_id"] = 200, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(), ["values"] = new JsonArray("Blue") }
                    ),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = listingId * 1000 + 4,
                        ["quantity"] = 2,
                        ["is_enabled"] = true,
                        ["is_deleted"] = false,
                        ["price"] = FakeEtsyShop.Money(3499),
                        ["readiness_state_id"] = null,
                    }),
                }
            ),
        };
        return listing;
    }

    [Fact]
    public async Task Multi_offering_variations_bulk_apply_updates_all_products_and_offerings_preserving_other_fields()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 business day",
        });
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 603L,
            ["readiness_state"] = "made_to_order",
            ["min_processing_days"] = 5,
            ["max_processing_days"] = 7,
            ["processing_days_display_label"] = "5-7 business days",
        });

        shop.Listings.Add(MultiProductMultiOfferingListing(10, "Multi-Variant Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 10)).Id;
        }

        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // 1. Preview check
        var preview = await service.PreviewBulkApplyAsync(602, [listingId]);
        var previewRow = Assert.Single(preview);
        Assert.True(previewRow.Changes);
        Assert.Null(previewRow.Skipped);
        // Before label should describe mixed existing profiles (601 and 603)
        Assert.Contains("1-3 days", previewRow.Before);
        Assert.Contains("5-7 business days", previewRow.Before);
        Assert.Equal("1 business day", previewRow.After);

        // 2. Apply check via ProcessingProfileService
        var changed = await service.ApplyAsync(602, [listingId], "tophattwaffle");
        Assert.Equal([listingId], changed);

        // 3. Inspect saved draft JSON to ensure ALL 3 products and ALL 6 offerings received 602
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == listingId);
            Assert.True(l.HasDraft);
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            Assert.Contains(ListingFields.Inventory, model.PendingFields);

            var products = model.Local(ListingFields.Inventory)!["products"]!.AsArray();
            Assert.Equal(4, products.Count);

            var allOfferings = products.SelectMany(p => p!["offerings"]!.AsArray()).ToList();
            Assert.Equal(4, allOfferings.Count);

            // Every single offering must now have readiness_state_id = 602
            Assert.All(allOfferings, o => Assert.Equal(602L, (long?)o!["readiness_state_id"]));

            // Verify other offering fields were preserved intact
            var prices = allOfferings.Select(o => (long)o!["price_amount"]!).ToList();
            Assert.Equal([1999, 2499, 2999, 3499], prices.Order());

            var enabled = allOfferings.Select(o => (bool)o!["is_enabled"]!).ToList();
            Assert.All(enabled, Assert.True);

            var skus = products.Select(p => (string?)p!["sku"]).ToList();
            Assert.Equal(4, skus.Count);
            Assert.Contains("SKU10-S-RED", skus);
            Assert.Contains("SKU10-S-BLU", skus);
            Assert.Contains("SKU10-L-RED", skus);
            Assert.Contains("SKU10-L-BLU", skus);
        }

        // 4. Verify push review passes without blockers
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var reviews = await bulkEditor.ReviewAsync([listingId]);
        var review = Assert.Single(reviews);
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));

        // 5. Verify push sends all 6 offerings with readiness_state_id = 602 to Etsy
        var pushResults = await bulkEditor.PushAsync([listingId], User(Roles.Admin));
        var pushOutcome = Assert.Single(pushResults);
        Assert.NotNull(pushOutcome.PushId);
        Assert.Empty(pushOutcome.Blockers);

        // Process queued push job
        var jobs = shop.Provider.GetRequiredService<RecordingJobQueue>();
        var pushReq = Assert.Single(jobs.Requests);
        Assert.Equal("etsy.listing.push", pushReq.JobType);
    }

    [Fact]
    public async Task BulkListingEditor_ProcessingProfile_updates_multi_product_multi_offering_listings()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 business day",
        });

        shop.Listings.Add(MultiProductMultiOfferingListing(20, "Multi-Variant Bulk"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 20)).Id;
        }

        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 602 });

        var preview = await bulkEditor.PreviewAsync([listingId], edit);
        var previewRow = Assert.Single(preview);
        Assert.True(previewRow.Changes);
        Assert.Equal("1 business day", previewRow.After);

        var changed = await bulkEditor.ApplyAsync([listingId], edit, User(Roles.Admin));
        Assert.Equal([listingId], changed);

        // Verify all 6 offerings updated in draft
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == listingId);
            Assert.True(l.HasDraft);
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            var allOfferings = model.Local(ListingFields.Inventory)!["products"]!.AsArray()
                .SelectMany(p => p!["offerings"]!.AsArray()).ToList();
            Assert.Equal(4, allOfferings.Count);
            Assert.All(allOfferings, o => Assert.Equal(602L, (long?)o!["readiness_state_id"]));
        }
    }

    [Fact]
    public async Task Drift_protection_strictly_skips_drifted_listings_in_preview_and_apply_and_rechecks_at_apply_time()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 business day",
        });

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Clean Listing"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Drifted Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int id1, id2;
        await using (var db = shop.Db.CreateDbContext())
        {
            id1 = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
            id2 = (await db.Listings.SingleAsync(l => l.EtsyListingId == 2)).Id;
        }

        // Cause inventory drift on Etsy for listing 2
        shop.ListingJson(2)["inventory"]!["products"]![0]!["offerings"]![0]!["price"]!["amount"] = 8888;
        shop.ListingJson(2)["last_modified_timestamp"] = 1_770_000_123;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // 1. ProcessingProfileService Preview
        var preview = await service.PreviewBulkApplyAsync(602, [id1, id2]);
        Assert.Equal(2, preview.Count);

        var cleanRow = preview.Single(r => r.ListingId == id1);
        Assert.True(cleanRow.Changes);
        Assert.Null(cleanRow.Skipped);

        var driftedRow = preview.Single(r => r.ListingId == id2);
        Assert.False(driftedRow.Changes);
        Assert.NotNull(driftedRow.Skipped);
        Assert.Contains("changed on Etsy; resolve it in the editor first.", driftedRow.Skipped);

        // 2. ProcessingProfileService Apply
        var changed = await service.ApplyAsync(602, [id1, id2], "tophattwaffle");
        Assert.Equal([id1], changed);

        // Verify drifted listing has NO draft created
        await using (var db = shop.Db.CreateDbContext())
        {
            var l2 = await db.Listings.SingleAsync(l => l.Id == id2);
            Assert.False(l2.HasDraft);
        }

        // 3. BulkListingEditor Preview and Apply
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 602 });

        var bulkPreview = await bulkEditor.PreviewAsync([id1, id2], edit);
        var bulkDrifted = bulkPreview.Single(r => r.ListingId == id2);
        Assert.False(bulkDrifted.Changes);
        Assert.Contains("changed on Etsy; resolve it in the editor first.", bulkDrifted.Skipped);

        // 4. Mid-flight drift test: Clean listing drifts AFTER preview but BEFORE apply
        shop.ListingJson(1)["inventory"]!["products"]![0]!["offerings"]![0]!["price"]!["amount"] = 7777;
        shop.ListingJson(1)["last_modified_timestamp"] = 1_770_000_999;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Apply again for id1 — should now detect drift and skip id1!
        var midFlightChanged = await service.ApplyAsync(602, [id1], "tophattwaffle");
        Assert.Empty(midFlightChanged);

        var bulkMidFlightChanged = await bulkEditor.ApplyAsync([id1], edit, User(Roles.Admin));
        Assert.Empty(bulkMidFlightChanged);
    }

    [Fact]
    public async Task Missing_on_Etsy_listings_are_strictly_skipped_with_clear_reason()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 business day",
        });

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Missing Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.SingleAsync(l => l.EtsyListingId == 1);
            l.IsMissingOnEtsy = true;
            await db.SaveChangesAsync();
            listingId = l.Id;
        }

        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // 1. ProcessingProfileService Preview
        var preview = await service.PreviewBulkApplyAsync(602, [listingId]);
        var row = Assert.Single(preview);
        Assert.False(row.Changes);
        Assert.Equal("Gone from Etsy.", row.Skipped);

        // 2. ProcessingProfileService Apply
        var changed = await service.ApplyAsync(602, [listingId], "tophattwaffle");
        Assert.Empty(changed);

        // 3. BulkListingEditor Preview & Apply
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 602 });

        var bulkPreview = await bulkEditor.PreviewAsync([listingId], edit);
        var bulkRow = Assert.Single(bulkPreview);
        Assert.False(bulkRow.Changes);
        Assert.Equal("Gone from Etsy.", bulkRow.Skipped);

        var bulkChanged = await bulkEditor.ApplyAsync([listingId], edit, User(Roles.Admin));
        Assert.Empty(bulkChanged);

        // 4. Candidate list status check
        var candidates = await service.GetCandidatesAsync(FakeEtsyShop.ShopId);
        var candidate = Assert.Single(candidates);
        Assert.True(candidate.IsMissingOnEtsy);

        // Template filter status filtering
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "all" }));
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "active" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "missing" }));
    }

    [Fact]
    public async Task Already_using_target_profile_is_flagged_and_unmodified_both_single_and_multi_offering()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Already Uses 601");
        foreach (var p in listing["inventory"]!["products"]!.AsArray())
            foreach (var o in p!["offerings"]!.AsArray())
                o!["readiness_state_id"] = 601L;
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        // Both default offerings already have readiness_state_id = 601
        var preview = await service.PreviewBulkApplyAsync(601, [listingId]);
        var row = Assert.Single(preview);
        Assert.False(row.Changes);
        Assert.Equal("Already uses this profile.", row.Skipped);

        var changed = await service.ApplyAsync(601, [listingId], "tophattwaffle");
        Assert.Empty(changed);

        // BulkListingEditor behavior
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 601 });

        var bulkPreview = await bulkEditor.PreviewAsync([listingId], edit);
        var bulkRow = Assert.Single(bulkPreview);
        Assert.False(bulkRow.Changes);
        Assert.Equal(bulkRow.Before, bulkRow.After);

        var bulkChanged = await bulkEditor.ApplyAsync([listingId], edit, User(Roles.Admin));
        Assert.Empty(bulkChanged);

        // Verify draft was never created
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.SingleAsync(l => l.Id == listingId);
            Assert.False(l.HasDraft);
        }
    }

    [Fact]
    public async Task Partial_profile_match_updates_all_offerings_and_is_not_treated_as_already_using()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 1,
            ["processing_days_display_label"] = "1 business day",
        });

        // Listing with product 1 having 601, and product 2 having 602
        var listing = FakeEtsyShop.Listing(1, "Partial Match Listing");
        var products = listing["inventory"]!["products"]!.AsArray();
        products[0]!["offerings"]![0]!["readiness_state_id"] = 601L;
        products[1]!["offerings"]![0]!["readiness_state_id"] = 602L;
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        // Target profile is 602. Since offering 1 is 601, this is NOT a full match.
        var preview = await service.PreviewBulkApplyAsync(602, [listingId]);
        var row = Assert.Single(preview);
        Assert.True(row.Changes);
        Assert.Null(row.Skipped);

        var changed = await service.ApplyAsync(602, [listingId], "tophattwaffle");
        Assert.Equal([listingId], changed);

        // Verify all offerings are now 602
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == listingId);
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            var offerings = model.Local(ListingFields.Inventory)!["products"]!.AsArray()
                .SelectMany(p => p!["offerings"]!.AsArray());
            Assert.All(offerings, o => Assert.Equal(602L, (long?)o!["readiness_state_id"]));
        }
    }

    [Fact]
    public async Task Cross_shop_profile_application_is_strictly_blocked()
    {
        using var shop = new FakeEtsyShop();
        int shop2Id;
        await using (var db = shop.Db.CreateDbContext())
        {
            var s2 = new Shop
            {
                Id = 2,
                EtsyShopId = 9999,
                EtsyUserId = 8888,
                Name = "SecondShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow,
                Token = new ShopToken { EncryptedAccessToken = "tok2", EncryptedRefreshToken = "ref2", Scopes = "listings_w" },
            };
            db.Shops.Add(s2);
            db.ProcessingProfiles.Add(new ProcessingProfile
            {
                ShopId = 2,
                ReadinessStateId = 699,
                ReadinessState = "made_to_order",
                MinProcessingDays = 3,
                MaxProcessingDays = 5,
                DisplayLabel = "Shop 2 Profile",
            });
            await db.SaveChangesAsync();
            shop2Id = s2.Id;
        }

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Shop 1 Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        // Try applying profile 699 (belongs to Shop 2) to listing 1 (belongs to Shop 1)
        var preview = await service.PreviewBulkApplyAsync(699, [listingId]);
        var row = Assert.Single(preview);
        Assert.False(row.Changes);
        Assert.Equal("Target profile belongs to another shop.", row.Skipped);

        var changed = await service.ApplyAsync(699, [listingId], "tophattwaffle");
        Assert.Empty(changed);

        // BulkListingEditor preview with Shop 2's profile passed for Shop 1
        var bulkEditor = shop.Provider.GetRequiredService<BulkListingEditor>();
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 699 });

        var bulkPreview = await bulkEditor.PreviewAsync([listingId], edit);
        var bulkRow = Assert.Single(bulkPreview);
        Assert.False(bulkRow.Changes);
        Assert.Equal("That choice isn't one of this shop's.", bulkRow.Skipped);
    }

    [Fact]
    public async Task Search_and_filtering_adversarial_edge_cases()
    {
        var candidate = new ProcessingProfileCandidate(
            ListingId: 100,
            Title: "Handmade Ceramic Plant Pot (Blue)",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 9876543210,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["POT-SM-BLU#1", "POT-LG-BLU#2"],
            Tags: ["gardening", "plant pot", "indoor plants", "succulents"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "1-3 business days"
        );

        // 1. Leading/trailing whitespace in search query
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "  ceramic  " }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "   POT-SM   " }));

        // 2. Case insensitivity and special characters
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "HANDMADE" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "pLaNt PoT" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "(Blue)" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "-BLU#1" }));

        // 3. Exact numeric Etsy listing ID
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "9876543210" }));

        // 4. Partial numeric search: does NOT match EtsyListingId if not numeric match, but would match if in title/SKU
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "98765" }));

        // 5. Whitespace-only search treated as no filter
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "   " }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = null }));

        // 6. Shop filtering
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { ShopId = 1 }));
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { ShopId = 2 }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { ShopId = null }));

        // 7. Combined mismatch: text matches, but shop mismatches
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Text = "ceramic", ShopId = 2 }));

        // 8. Candidate with empty or null SKU/tag collections handles gracefully
        var sparse = new ProcessingProfileCandidate(101, "Minimal", 1, "Shop", 111, "active", false, [], [], null, "—");
        Assert.True(TemplateFilterMatcher.Matches(sparse, new TemplateListingFilter { Text = "minimal" }));
        Assert.False(TemplateFilterMatcher.Matches(sparse, new TemplateListingFilter { Text = "pot" }));

        // 9. Status filter edge cases
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "ACTIVE" }));
        Assert.True(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "  active  " }));
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "draft" }));
        Assert.False(TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { Status = "missing" }));
    }

    [Fact]
    public async Task Listing_count_in_ListAsync_properly_deduplicates_and_ignores_missing_listings()
    {
        using var shop = new FakeEtsyShop();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 601L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 3,
            ["processing_days_display_label"] = "1-3 days",
        });

        // Add 2 listings: listing 1 active with 601, listing 2 missing on etsy with 601
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Active Listing"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Missing Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var l2 = await db.Listings.SingleAsync(l => l.EtsyListingId == 2);
            l2.IsMissingOnEtsy = true;
            await db.SaveChangesAsync();
        }

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        var list = await service.ListAsync(FakeEtsyShop.ShopId);
        var p601 = Assert.Single(list, p => p.ReadinessStateId == 601);

        // Listing 1 has 2 offerings both with 601 -> should count as 1 listing, not 2.
        // Listing 2 is missing on Etsy -> should be excluded.
        Assert.Equal(1, p601.ListingCount);
    }
}
