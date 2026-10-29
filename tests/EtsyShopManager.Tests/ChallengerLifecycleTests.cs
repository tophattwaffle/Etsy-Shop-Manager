using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Adversarial empirical tests verifying the Order Personalization Verification Lifecycle,
/// SHA-256 Dismissal Hashing, Master Toggle Suppression/Re-enablement, Open Orders Re-evaluation triggers,
/// and Home Assistant payload contracts and backwards compatibility.
/// </summary>
public class ChallengerLifecycleTests
{
    private sealed class InMemoryJobQueue : IJobQueue
    {
        public List<JobRequest> Queued { get; } = [];

        public Task<long?> EnqueueAsync(JobRequest request, CancellationToken ct = default)
        {
            Queued.Add(request);
            return Task.FromResult<long?>((long)Queued.Count);
        }

        public Task<bool> RetryAsync(long jobId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CancelAsync(long jobId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class InMemoryAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, string? userName = null, CancellationToken ct = default)
        {
            Entries.Add(new AuditEntry
            {
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                UserName = userName,
                AtUtc = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public Dictionary<string, string?> Settings { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Settings.GetValueOrDefault(key));

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(!string.IsNullOrWhiteSpace(Settings.GetValueOrDefault(key)));

        public Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Settings.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static PersonalizationVerificationService CreateService(
        TestDb db,
        InMemorySettingsStore settings,
        InMemoryJobQueue? queue = null,
        InMemoryAuditLog? audit = null,
        TimeProvider? time = null)
    {
        var colorService = new OrderColorService(settings);
        return new PersonalizationVerificationService(
            db,
            settings,
            colorService,
            queue ?? new InMemoryJobQueue(),
            audit ?? new InMemoryAuditLog(),
            time ?? new ImmediateTimeProvider(),
            NullLogger<PersonalizationVerificationService>.Instance);
    }

    // =========================================================================
    // 1. Stress Test: Dismissal Lifecycle & SHA-256 Hashing
    // =========================================================================

    [Fact]
    public async Task StressTest_DismissalLifecycle_SingleItem_FullCycle_Alert_Dismiss_Mutate_Restore()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var audit = new InMemoryAuditLog();
        var service = CreateService(db, settings, audit: audit);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Personalized Sign" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Two Colors Required",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 2,
                MaxColors = 2,
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5001,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Personalized Sign",
                        Quantity = 1,
                        Variations =
                        [
                            new OrderVariation
                            {
                                PropertyId = HaPayloadBuilder.PersonalizationPropertyId,
                                Name = "Personalization",
                                Value = "Copper only"
                            }
                        ]
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        // Step 1: Initial verification runs -> rule fails (only 1 color found: Copper)
        await service.VerifyOrderAsync(1);

        string h1;
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.NotNull(order.PersonalizationCheckAlert);
            Assert.Contains("Two Colors Required", order.PersonalizationCheckAlert);
            Assert.NotNull(order.PersonalizationCheckHash);
            Assert.Null(order.PersonalizationCheckDismissedHash);
            Assert.True(order.HasActivePersonalizationAlert);
            h1 = order.PersonalizationCheckHash;
        }

        // Step 2: Dismiss alert via "Looks fine" button -> dismissed hash == H1
        var dismissed = await service.DismissAlertAsync(1, "seller_user");
        Assert.True(dismissed);

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal(h1, order.PersonalizationCheckHash);
            Assert.Equal(h1, order.PersonalizationCheckDismissedHash);
            Assert.False(order.HasActivePersonalizationAlert, "Alert must be dismissed when check hash == dismissed hash.");
        }

        // Step 3: Seller updates personalization via OrderOverrideService to different failing text
        var overrideService = new OrderOverrideService(db, audit, personalizationVerification: service);
        var form = await overrideService.GetFormAsync(1);
        Assert.NotNull(form);
        form.Items[0].ChangePersonalization = true;
        form.Items[0].Personalization = "Copper and some non-color item";
        var saveResult = await overrideService.SaveAsync(form, "seller_user");
        Assert.Null(saveResult);

        // Verification must automatically execute and compute new hash H2 != H1
        string h2;
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            h2 = order.PersonalizationCheckHash!;
            Assert.NotNull(h2);
            Assert.NotEqual(h1, h2);
            Assert.Equal(h1, order.PersonalizationCheckDismissedHash);
            Assert.True(order.HasActivePersonalizationAlert, "Alert MUST immediately re-appear because H2 != H1 dismissed hash!");
        }

        // Step 4: Seller modifies personalization back to original dismissed text ("Copper only")
        form = await overrideService.GetFormAsync(1);
        Assert.NotNull(form);
        form.Items[0].ChangePersonalization = false; // restores buyer's text
        saveResult = await overrideService.SaveAsync(form, "seller_user");
        Assert.Null(saveResult);

        // Verification re-evaluates -> Hash must be restored to H1 == H1_dismissed -> Alert remains dismissed!
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.Equal(h1, order.PersonalizationCheckHash);
            Assert.Equal(h1, order.PersonalizationCheckDismissedHash);
            Assert.False(order.HasActivePersonalizationAlert, "Alert MUST be dismissed again when text restores original hash H1 == H1_dismissed!");
        }
    }

    [Fact]
    public async Task StressTest_DismissalLifecycle_MultiItem_CanonicalOrdering_And_PartialOverrides()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var audit = new InMemoryAuditLog();
        var service = CreateService(db, settings, audit: audit);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "NotEmpty",
                RuleType = PersonalizationRuleType.NotEmpty,
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            // Two items with transaction IDs deliberately out of order in database
            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5002,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 20,
                        OrderId = 1,
                        EtsyTransactionId = 9999, // higher transaction id first
                        EtsyListingId = 1001,
                        Title = "Widget B",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Text B" }]
                    },
                    new OrderItem
                    {
                        Id = 10,
                        OrderId = 1,
                        EtsyTransactionId = 1111, // lower transaction id second
                        EtsyListingId = 1001,
                        Title = "Widget A",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "   " }] // fails NotEmpty
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        await service.VerifyOrderAsync(1);

        string h1;
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.Include(o => o.Items).FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.True(order.HasActivePersonalizationAlert);
            h1 = order.PersonalizationCheckHash!;

            // Verify canonical hash computation is deterministic and sorted by transaction ID
            var canonicalExpected = $"1111:\n9999:Text B";
            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalExpected))).ToLowerInvariant();
            Assert.Equal(expectedHash, h1);
        }

        // Dismiss the alert for this combined state
        await service.DismissAlertAsync(1, "seller");

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.False(order.HasActivePersonalizationAlert);
        }

        // Now modify only item 20 (Widget B) personalization
        var overrideService = new OrderOverrideService(db, audit, personalizationVerification: service);
        var form = await overrideService.GetFormAsync(1);
        Assert.NotNull(form);
        var itemB = form.Items.First(i => i.ItemId == 20);
        itemB.ChangePersonalization = true;
        itemB.Personalization = "Text B Modified";
        await overrideService.SaveAsync(form, "seller");

        // Hash must change and alert must reactivate because Item A still fails!
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.NotEqual(h1, order.PersonalizationCheckHash);
            Assert.True(order.HasActivePersonalizationAlert);
        }

        // Restore item 20 back to "Text B"
        form = await overrideService.GetFormAsync(1);
        Assert.NotNull(form);
        itemB = form.Items.First(i => i.ItemId == 20);
        itemB.ChangePersonalization = false;
        await overrideService.SaveAsync(form, "seller");

        // Dismissal must be restored
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal(h1, order.PersonalizationCheckHash);
            Assert.False(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task StressTest_DismissalLifecycle_WhitespaceNormalization()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var audit = new InMemoryAuditLog();
        var service = CreateService(db, settings, audit: audit);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Word List",
                RuleType = PersonalizationRuleType.WordList,
                WordList = "alpha, beta",
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            // Buyer entered failing text with leading and trailing whitespaces
            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5003,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "  gamma delta  " }]
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        await service.VerifyOrderAsync(1);
        await service.DismissAlertAsync(1, "seller");

        string dismissedHash;
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.False(order.HasActivePersonalizationAlert);
            dismissedHash = order.PersonalizationCheckDismissedHash!;
        }

        // Computing hash on trimmed text yields identical hash
        var trimmedCanonical = "9001:gamma delta";
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimmedCanonical))).ToLowerInvariant();
        Assert.Equal(expectedHash, dismissedHash);
    }

    // =========================================================================
    // 2. Stress Test: Master Toggle
    // =========================================================================

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public async Task StressTest_MasterToggle_WhenDisabled_VerifyOrderAsync_SkipsEvaluation(string disabledValue)
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, disabledValue);
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "NotEmpty",
                RuleType = PersonalizationRuleType.NotEmpty,
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5004,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "" }]
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        await service.VerifyOrderAsync(1);

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Skipped", order.PersonalizationCheckStatus);
            Assert.Null(order.PersonalizationCheckAlert);
            Assert.NotNull(order.PersonalizationCheckHash);
            Assert.False(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task StressTest_MasterToggle_ToggleOff_ClearsAlerts_And_ToggleOn_RestoresAlerts()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "NotEmpty",
                RuleType = PersonalizationRuleType.NotEmpty,
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5005,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "" }]
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        // 1. Initial recheck with toggle enabled (default)
        await service.RecheckAllOpenOrdersAsync();

        Order failingOrder;
        await using (var ctx = db.CreateDbContext())
        {
            failingOrder = await ctx.Orders.Include(o => o.Items).FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", failingOrder.PersonalizationCheckStatus);
            Assert.True(failingOrder.HasActivePersonalizationAlert);
        }

        // Verify HA payload has active alert
        var payloadActive = HaPayloadBuilder.OpenOrders([failingOrder], TimeZoneInfo.Utc);
        Assert.Single(payloadActive["personalization_alerts"]!.AsArray());
        Assert.True(payloadActive["open_orders"]!.AsArray()[0]!.AsObject().ContainsKey("personalization_alert"));

        // 2. Toggle disabled: set "0" and recheck open orders
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, "0");
        await service.RecheckAllOpenOrdersAsync();

        Order skippedOrder;
        await using (var ctx = db.CreateDbContext())
        {
            skippedOrder = await ctx.Orders.Include(o => o.Items).FirstAsync(o => o.Id == 1);
            Assert.Equal("Skipped", skippedOrder.PersonalizationCheckStatus);
            Assert.Null(skippedOrder.PersonalizationCheckAlert);
            Assert.False(skippedOrder.HasActivePersonalizationAlert);
        }

        // Verify HA payload suppresses alerts when disabled
        var payloadSkipped = HaPayloadBuilder.OpenOrders([skippedOrder], TimeZoneInfo.Utc);
        Assert.Empty(payloadSkipped["personalization_alerts"]!.AsArray());
        Assert.False(payloadSkipped["open_orders"]!.AsArray()[0]!.AsObject().ContainsKey("personalization_alert"));

        // 3. Toggle back ON: set "1" and recheck open orders
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, "1");
        await service.RecheckAllOpenOrdersAsync();

        Order restoredOrder;
        await using (var ctx = db.CreateDbContext())
        {
            restoredOrder = await ctx.Orders.Include(o => o.Items).FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", restoredOrder.PersonalizationCheckStatus);
            Assert.NotNull(restoredOrder.PersonalizationCheckAlert);
            Assert.True(restoredOrder.HasActivePersonalizationAlert);
        }

        // Verify HA payload restored
        var payloadRestored = HaPayloadBuilder.OpenOrders([restoredOrder], TimeZoneInfo.Utc);
        Assert.Single(payloadRestored["personalization_alerts"]!.AsArray());
        Assert.True(payloadRestored["open_orders"]!.AsArray()[0]!.AsObject().ContainsKey("personalization_alert"));
    }

    [Fact]
    public async Task StressTest_MasterToggle_PreservesDismissalAcrossToggleCycles()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "NotEmpty",
                RuleType = PersonalizationRuleType.NotEmpty,
            };
            var link = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            var order = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5006,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "" }]
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.Add(listing);
            ctx.PersonalizationRules.Add(rule);
            ctx.ListingPersonalizationRules.Add(link);
            ctx.Orders.Add(order);
            await ctx.SaveChangesAsync();
        }

        // Fails initially
        await service.VerifyOrderAsync(1);
        // User dismisses
        await service.DismissAlertAsync(1, "seller");

        string dismissedHash;
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.False(order.HasActivePersonalizationAlert);
            dismissedHash = order.PersonalizationCheckDismissedHash!;
            Assert.NotNull(dismissedHash);
        }

        // Turn toggle OFF
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, "0");
        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Skipped", order.PersonalizationCheckStatus);
            Assert.Equal(dismissedHash, order.PersonalizationCheckDismissedHash); // preserved!
        }

        // Turn toggle back ON
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, "1");
        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.Equal(dismissedHash, order.PersonalizationCheckDismissedHash);
            Assert.Equal(dismissedHash, order.PersonalizationCheckHash);
            // Crucial: The dismissal MUST NOT be forgotten just because verification was toggled!
            Assert.False(order.HasActivePersonalizationAlert, "Dismissal must be preserved across master toggle enable/disable cycles.");
        }
    }

    // =========================================================================
    // 3. Stress Test: Open Orders Re-evaluation
    // =========================================================================

    [Fact]
    public async Task StressTest_OpenOrdersReevaluation_RuleMutation_TightenRelax_And_Unlink()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listingA = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Listing A" };
            var listingB = new Listing { Id = 2, ShopId = 1, EtsyListingId = 1002, Title = "Listing B" };

            var rule1 = new PersonalizationRule
            {
                Id = 1,
                Name = "Color Rule",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 1,
            };

            var link1 = new ListingPersonalizationRule { Id = 1, ListingId = 1, PersonalizationRuleId = 1 };

            // Order 1 on Listing A: has 1 color ("Blue") -> passes initial MinColors 1
            var order1 = new Order
            {
                Id = 1,
                ShopId = 1,
                EtsyReceiptId = 5007,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 1,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 1001,
                        Title = "Listing A Item",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Blue" }]
                    }
                ]
            };

            // Order 2 on Listing B: has no rules initially
            var order2 = new Order
            {
                Id = 2,
                ShopId = 1,
                EtsyReceiptId = 5008,
                IsPaid = true,
                IsShipped = false,
                Status = "open",
                Items =
                [
                    new OrderItem
                    {
                        Id = 2,
                        OrderId = 2,
                        EtsyTransactionId = 9002,
                        EtsyListingId = 1002,
                        Title = "Listing B Item",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Anything" }]
                    }
                ]
            };

            // Order 3 on Listing A: already shipped (closed order)
            var order3 = new Order
            {
                Id = 3,
                ShopId = 1,
                EtsyReceiptId = 5009,
                IsPaid = true,
                IsShipped = true, // Shipped!
                Status = "completed",
                Items =
                [
                    new OrderItem
                    {
                        Id = 3,
                        OrderId = 3,
                        EtsyTransactionId = 9003,
                        EtsyListingId = 1001,
                        Title = "Shipped Item",
                        Quantity = 1,
                        Variations = []
                    }
                ]
            };

            // Order 4 on Listing A: canceled (closed order)
            var order4 = new Order
            {
                Id = 4,
                ShopId = 1,
                EtsyReceiptId = 5010,
                IsPaid = true,
                IsShipped = false,
                Status = "canceled", // Canceled!
                Items =
                [
                    new OrderItem
                    {
                        Id = 4,
                        OrderId = 4,
                        EtsyTransactionId = 9004,
                        EtsyListingId = 1001,
                        Title = "Canceled Item",
                        Quantity = 1,
                        Variations = []
                    }
                ]
            };

            ctx.Shops.Add(shop);
            ctx.Listings.AddRange(listingA, listingB);
            ctx.PersonalizationRules.Add(rule1);
            ctx.ListingPersonalizationRules.Add(link1);
            ctx.Orders.AddRange(order1, order2, order3, order4);
            await ctx.SaveChangesAsync();
        }

        // Initial recheck: Order 1 Ok, Order 2 Ok (no rules), Orders 3 & 4 not rechecked because not open!
        var count = await service.RecheckAllOpenOrdersAsync();
        Assert.Equal(2, count); // exactly 2 open orders rechecked

        await using (var ctx = db.CreateDbContext())
        {
            var o1 = await ctx.Orders.FirstAsync(o => o.Id == 1);
            var o2 = await ctx.Orders.FirstAsync(o => o.Id == 2);
            var o3 = await ctx.Orders.FirstAsync(o => o.Id == 3);
            var o4 = await ctx.Orders.FirstAsync(o => o.Id == 4);

            Assert.Equal("Ok", o1.PersonalizationCheckStatus);
            Assert.Equal("Ok", o2.PersonalizationCheckStatus);
            Assert.Null(o3.PersonalizationCheckStatus); // untouched
            Assert.Null(o4.PersonalizationCheckStatus); // untouched
        }

        // TIGHTEN RULE: Change MinColors from 1 to 2
        await using (var ctx = db.CreateDbContext())
        {
            var r = await ctx.PersonalizationRules.FirstAsync(r => r.Id == 1);
            r.MinColors = 2;
            await ctx.SaveChangesAsync();
        }

        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var o1 = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", o1.PersonalizationCheckStatus);
            Assert.True(o1.HasActivePersonalizationAlert);
        }

        // LINK LISTING B TO A NEW FAILING RULE (NotEmpty)
        await using (var ctx = db.CreateDbContext())
        {
            var r2 = new PersonalizationRule
            {
                Id = 2,
                Name = "Must mention Cat",
                RuleType = PersonalizationRuleType.WordList,
                WordList = "cat, kitten",
            };
            ctx.PersonalizationRules.Add(r2);
            ctx.ListingPersonalizationRules.Add(new ListingPersonalizationRule
            {
                Id = 2,
                ListingId = 2,
                PersonalizationRuleId = 2,
            });
            await ctx.SaveChangesAsync();
        }

        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var o2 = await ctx.Orders.FirstAsync(o => o.Id == 2);
            Assert.Equal("Failed", o2.PersonalizationCheckStatus);
            Assert.True(o2.HasActivePersonalizationAlert);
        }

        // UNLINK LISTING B FROM RULE 2
        await using (var ctx = db.CreateDbContext())
        {
            var link = await ctx.ListingPersonalizationRules.FirstAsync(l => l.ListingId == 2 && l.PersonalizationRuleId == 2);
            ctx.ListingPersonalizationRules.Remove(link);
            await ctx.SaveChangesAsync();
        }

        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var o2 = await ctx.Orders.FirstAsync(o => o.Id == 2);
            Assert.Equal("Ok", o2.PersonalizationCheckStatus);
            Assert.False(o2.HasActivePersonalizationAlert);
        }

        // RELAX RULE 1 back to MinColors = 1
        await using (var ctx = db.CreateDbContext())
        {
            var r = await ctx.PersonalizationRules.FirstAsync(r => r.Id == 1);
            r.MinColors = 1;
            await ctx.SaveChangesAsync();
        }

        await service.RecheckAllOpenOrdersAsync();

        await using (var ctx = db.CreateDbContext())
        {
            var o1 = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Ok", o1.PersonalizationCheckStatus);
            Assert.False(o1.HasActivePersonalizationAlert);
        }
    }

    // =========================================================================
    // 4. Stress Test: Home Assistant Integration & Backwards Compatibility
    // =========================================================================

    [Fact]
    public void StressTest_HomeAssistantPayload_ActiveAlert_StructureAndTypes()
    {
        var order = new Order
        {
            Id = 77,
            ShopId = 1,
            EtsyReceiptId = 987654321,
            BuyerName = "Adversarial Buyer",
            BuyerUserId = 12345,
            Status = "open",
            IsPaid = true,
            IsShipped = false,
            CurrencyCode = "USD",
            Divisor = 100,
            Subtotal = 2500,
            Grandtotal = 2500,
            PersonalizationCheckStatus = "Failed",
            PersonalizationCheckAlert = "Failed rule 'Two Colors': Expected 2 colors, found 1.",
            PersonalizationCheckHash = "sha256_active_hash",
            PersonalizationCheckDismissedHash = null,
            Items =
            [
                new OrderItem
                {
                    Id = 10,
                    OrderId = 77,
                    EtsyTransactionId = 555,
                    Title = "Test Item",
                    Quantity = 1,
                    PriceAmount = 2500,
                    PriceDivisor = 100,
                    CurrencyCode = "USD",
                }
            ]
        };

        Assert.True(order.HasActivePersonalizationAlert);

        var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);

        // 1. Top-level keys verification
        Assert.True(payload.ContainsKey("personalization_alerts"));
        Assert.True(payload.ContainsKey("personalization_alert_css"));

        var alertsArray = payload["personalization_alerts"]?.AsArray();
        Assert.NotNull(alertsArray);
        Assert.Single(alertsArray);

        var alertEntry = alertsArray[0]?.AsObject();
        Assert.NotNull(alertEntry);
        Assert.Equal("987654321", alertEntry["receipt_id"]?.GetValue<string>());
        Assert.Equal(77, alertEntry["order_id"]?.GetValue<int>());
        Assert.Equal("Adversarial Buyer", alertEntry["buyer_name"]?.GetValue<string>());
        Assert.Equal("Failed", alertEntry["status"]?.GetValue<string>());
        Assert.Equal("Failed rule 'Two Colors': Expected 2 colors, found 1.", alertEntry["message"]?.GetValue<string>());

        var css = payload["personalization_alert_css"]?.GetValue<string>();
        Assert.NotNull(css);
        Assert.Contains(".personalization-alerts-blurb", css);
        Assert.Contains(".personalization-alert", css);

        // 2. Per-receipt summary verification
        var summaries = payload["open_orders"]?.AsArray();
        Assert.NotNull(summaries);
        Assert.Single(summaries);

        var summary = summaries[0]?.AsObject();
        Assert.NotNull(summary);
        Assert.True(summary.ContainsKey("personalization_alert"));

        var alertObj = summary["personalization_alert"]?.AsObject();
        Assert.NotNull(alertObj);
        Assert.Equal("Failed", alertObj["status"]?.GetValue<string>());
        Assert.Equal("error", alertObj["severity"]?.GetValue<string>());
        Assert.Equal("Failed rule 'Two Colors': Expected 2 colors, found 1.", alertObj["message"]?.GetValue<string>());
    }

    [Fact]
    public void StressTest_HomeAssistantPayload_DismissedOrNoAlert_OmittedCompletely()
    {
        var order = new Order
        {
            Id = 88,
            ShopId = 1,
            EtsyReceiptId = 987654322,
            BuyerName = "Dismissed Buyer",
            Status = "open",
            IsPaid = true,
            IsShipped = false,
            PersonalizationCheckStatus = "Failed",
            PersonalizationCheckAlert = "Some alert",
            PersonalizationCheckHash = "hash_xyz",
            PersonalizationCheckDismissedHash = "hash_xyz", // Dismissed!
            Items =
            [
                new OrderItem
                {
                    Id = 11,
                    OrderId = 88,
                    EtsyTransactionId = 556,
                    Title = "Item 2",
                    Quantity = 1,
                    PriceAmount = 1000,
                    PriceDivisor = 100,
                }
            ]
        };

        Assert.False(order.HasActivePersonalizationAlert);

        var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);

        // Top-level alerts array must be empty
        var alertsArray = payload["personalization_alerts"]?.AsArray();
        Assert.NotNull(alertsArray);
        Assert.Empty(alertsArray);

        // Per-receipt summary must NOT contain the personalization_alert key at all (omitted, not null)
        var summary = payload["open_orders"]?.AsArray()?[0]?.AsObject();
        Assert.NotNull(summary);
        Assert.False(summary.ContainsKey("personalization_alert"),
            "Key 'personalization_alert' must be completely omitted from summary when alert is not active.");
    }

    [Fact]
    public void StressTest_HomeAssistantPayload_BackwardsCompatibility_Preserved()
    {
        var order = new Order
        {
            Id = 99,
            ShopId = 1,
            EtsyReceiptId = 1234567890,
            BuyerUserId = 998877,
            BuyerName = "Legacy Sensor Consumer",
            Status = "open",
            IsPaid = true,
            IsShipped = false,
            Divisor = 100,
            Subtotal = 3500,
            Grandtotal = 3500,
            EtsyCreatedAtUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new OrderItem
                {
                    Id = 12,
                    OrderId = 99,
                    EtsyTransactionId = 777,
                    Title = "Standard Item",
                    Quantity = 2,
                    PriceAmount = 1750,
                    PriceDivisor = 100,
                    CurrencyCode = "USD",
                    Variations =
                    [
                        new OrderVariation { PropertyId = 100, Name = "Size", Value = "Large" },
                        new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Custom Name" }
                    ]
                }
            ]
        };

        var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);

        // Verify root legacy contracts
        Assert.Equal(1, payload["open_orders_count"]?.GetValue<int>());
        Assert.Equal(2, payload["total_items_needed"]?.GetValue<int>());
        Assert.NotNull(payload["items_needed"]?.AsArray());
        Assert.NotNull(payload["open_orders"]?.AsArray());
        Assert.NotNull(payload["address_alerts"]?.AsArray());
        Assert.NotNull(payload["address_alert_css"]?.GetValue<string>());

        var summary = payload["open_orders"]!.AsArray()[0]!.AsObject();
        Assert.Equal("1234567890", summary["receipt_id"]?.GetValue<string>());
        Assert.Equal("998877", summary["buyer_user_id"]?.GetValue<string>());
        Assert.Equal(35.0, summary["order_total"]?.GetValue<double>());
        Assert.Equal("USD", summary["currency_code"]?.GetValue<string>());
        Assert.Equal(1, summary["item_count"]?.GetValue<int>());
        Assert.Equal("open", summary["status"]?.GetValue<string>());
        Assert.True(summary["is_paid"]?.GetValue<bool>());
        Assert.False(summary["is_shipped"]?.GetValue<bool>());

        var item = summary["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal("777", item["transaction_id"]?.GetValue<string>());
        Assert.Equal("Standard Item", item["title"]?.GetValue<string>());
        Assert.Equal(2, item["quantity"]?.GetValue<int>());
        Assert.Equal(17.5, item["price_amount"]?.GetValue<double>());
        Assert.Equal("Custom Name", item["personalization"]?.GetValue<string>());

        // Verify OrderColorEngine.Paint works cleanly over this payload without exception
        var colorEngine = new OrderColorEngine(new OrderColorConfig([], []));
        colorEngine.Paint(payload);
    }
}
