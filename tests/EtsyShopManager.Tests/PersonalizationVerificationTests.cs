using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class PersonalizationVerificationTests
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

    [Fact]
    public async Task VerifyOrderAsync_sets_status_ok_when_rules_pass()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Min 1 color",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 1,
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Blue" }]
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
            Assert.Equal("Ok", order.PersonalizationCheckStatus);
            Assert.Null(order.PersonalizationCheckAlert);
            Assert.NotNull(order.PersonalizationCheckHash);
            Assert.False(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task VerifyOrderAsync_sets_status_failed_when_rules_fail()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Min 1 color",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 1,
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Plain no color" }]
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
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.NotNull(order.PersonalizationCheckAlert);
            Assert.Contains("Min 1 color", order.PersonalizationCheckAlert);
            Assert.True(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task DismissAlert_dismisses_until_personalization_changes()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var audit = new InMemoryAuditLog();
        var service = CreateService(db, settings, audit: audit);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Color count",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 2,
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Copper only" }]
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

        // 1. Initial verification -> fails
        await service.VerifyOrderAsync(1);
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.True(order.HasActivePersonalizationAlert);
        }

        // 2. Click "Looks fine" -> dismisses alert
        var dismissed = await service.DismissAlertAsync(1, "tester");
        Assert.True(dismissed);

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal(order.PersonalizationCheckHash, order.PersonalizationCheckDismissedHash);
            Assert.False(order.HasActivePersonalizationAlert);
        }

        // 3. Seller modifies personalization via OrderOverrideService -> changes text
        var overrideService = new OrderOverrideService(db, audit, personalizationVerification: service);
        var form = await overrideService.GetFormAsync(1);
        Assert.NotNull(form);
        form.Items[0].ChangePersonalization = true;
        form.Items[0].Personalization = "Copper and some non-color";
        await overrideService.SaveAsync(form, "tester");

        // 4. Verification re-ran automatically with new text -> new hash invalidates dismissal!
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.NotEqual(order.PersonalizationCheckHash, order.PersonalizationCheckDismissedHash);
            Assert.True(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task Master_toggle_off_suppresses_alerts_and_skips_verification()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        await settings.SetAsync(SettingKeys.PersonalizationVerificationEnabled, "0");
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Min 1 color",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 1,
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "No color" }]
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
            Assert.False(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task RecheckAllOpenOrdersAsync_updates_open_orders_when_rules_change()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
            var rule = new PersonalizationRule
            {
                Id = 1,
                Name = "Color Count",
                RuleType = PersonalizationRuleType.ColorCount,
                MinColors = 2,
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = [new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = "Blue" }]
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

        // Fails initially (expects 2 colors, only Blue present)
        await service.RecheckAllOpenOrdersAsync();
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.True(order.HasActivePersonalizationAlert);
        }

        // Change rule to require only 1 color
        await using (var ctx = db.CreateDbContext())
        {
            var rule = await ctx.PersonalizationRules.FirstAsync(r => r.Id == 1);
            rule.MinColors = 1;
            await ctx.SaveChangesAsync();
        }

        // Recheck all open orders
        var count = await service.RecheckAllOpenOrdersAsync();
        Assert.Equal(1, count);

        // Alert is cleared!
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Ok", order.PersonalizationCheckStatus);
            Assert.Null(order.PersonalizationCheckAlert);
            Assert.False(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public async Task OrderReceived_handler_verifies_order()
    {
        using var db = new TestDb();
        var settings = new InMemorySettingsStore();
        var service = CreateService(db, settings);
        var handler = new OrderReceivedPersonalizationVerificationHandler(service);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = new Shop { Id = 1, Name = "Test Shop", EtsyShopId = 101 };
            var listing = new Listing { Id = 1, ShopId = 1, EtsyListingId = 1001, Title = "Custom Widget" };
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
                        Title = "Custom Widget",
                        Quantity = 1,
                        Variations = []
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

        await handler.HandleAsync(new OrderReceived(1, 1, 5001), CancellationToken.None);

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Failed", order.PersonalizationCheckStatus);
            Assert.True(order.HasActivePersonalizationAlert);
        }
    }

    [Fact]
    public void HaPayloadBuilder_includes_personalization_alerts_and_receipt_summary()
    {
        var order = new Order
        {
            Id = 42,
            ShopId = 1,
            EtsyReceiptId = 9001,
            BuyerName = "Jane Doe",
            Status = "open",
            IsPaid = true,
            IsShipped = false,
            PersonalizationCheckStatus = "Failed",
            PersonalizationCheckAlert = "Rule 'Color Count': Expected 2 colors, found 1.",
            PersonalizationCheckHash = "hash123",
            Items =
            [
                new OrderItem
                {
                    Id = 1,
                    OrderId = 42,
                    EtsyTransactionId = 101,
                    Title = "Item 1",
                    Quantity = 1,
                }
            ]
        };

        // Active alert: HasActivePersonalizationAlert is true
        Assert.True(order.HasActivePersonalizationAlert);

        var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);

        // Top-level alerts array
        var alerts = payload["personalization_alerts"]?.AsArray();
        Assert.NotNull(alerts);
        Assert.Single(alerts);
        Assert.Equal("9001", alerts[0]!["receipt_id"]?.GetValue<string>());
        Assert.Equal(42, alerts[0]!["order_id"]?.GetValue<int>());
        Assert.Equal("Jane Doe", alerts[0]!["buyer_name"]?.GetValue<string>());
        Assert.Equal("Failed", alerts[0]!["status"]?.GetValue<string>());
        Assert.Contains("Expected 2 colors", alerts[0]!["message"]?.GetValue<string>());

        // Top-level alert css
        Assert.Equal(HaPayloadBuilder.PersonalizationAlertCss, payload["personalization_alert_css"]?.GetValue<string>());

        // Per-receipt summary
        var orderSummary = payload["open_orders"]?.AsArray()?[0]?.AsObject();
        Assert.NotNull(orderSummary);
        Assert.True(orderSummary.ContainsKey("personalization_alert"));
        var alertObj = orderSummary["personalization_alert"]?.AsObject();
        Assert.NotNull(alertObj);
        Assert.Equal("Failed", alertObj["status"]?.GetValue<string>());
        Assert.Equal("error", alertObj["severity"]?.GetValue<string>());
        Assert.Contains("Expected 2 colors", alertObj["message"]?.GetValue<string>());

        // When dismissed, alert is omitted from summary and top-level array is empty
        order.PersonalizationCheckDismissedHash = order.PersonalizationCheckHash;
        Assert.False(order.HasActivePersonalizationAlert);

        var dismissedPayload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);
        var dismissedAlerts = dismissedPayload["personalization_alerts"]?.AsArray();
        Assert.NotNull(dismissedAlerts);
        Assert.Empty(dismissedAlerts);

        var dismissedSummary = dismissedPayload["open_orders"]?.AsArray()?[0]?.AsObject();
        Assert.NotNull(dismissedSummary);
        Assert.False(dismissedSummary.ContainsKey("personalization_alert"));
    }
}
