using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Usps;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

public class AddressVerificationTests
{
    private sealed class InMemoryJobQueue : IJobQueue
    {
        public List<JobRequest> Queued { get; } = [];

        public Task<long?> EnqueueAsync(JobRequest request, CancellationToken ct = default)
        {
            Queued.Add(request);
            return Task.FromResult<long?>((long)Queued.Count);
        }

        public Task<bool> RetryAsync(long jobId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> CancelAsync(long jobId, CancellationToken ct = default) =>
            Task.FromResult(true);
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

    private static (UspsClient Client, ScriptedHandler Handler) CreateUspsClient(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var http = new HttpClient(handler);
        var options = Options.Create(new UspsOptions());
        var time = new ImmediateTimeProvider();
        return (new UspsClient(http, options, time, NullLogger<UspsClient>.Instance), handler);
    }

    [Fact]
    public void Classifier_identifies_HardAlert_for_AddressNotFound()
    {
        var input = new ParsedAddress("Buyer", "999 Fake Rd", null, "Nowhere", "AK", "99999", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                DPVConfirmation = "N"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.AddressNotFound, classified.Status);
        Assert.Equal("Address not found by USPS", classified.Alert);
        Assert.True(classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_identifies_HardAlert_for_MissingUnit()
    {
        var input = new ParsedAddress("Buyer", "100 Main St", null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "100 MAIN ST",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "D"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.MissingUnit, classified.Status);
        Assert.Equal("Apartment or unit missing", classified.Alert);
        Assert.NotNull(classified.SuggestedAddress);
    }

    [Fact]
    public void Classifier_identifies_HardAlert_for_UnitNotFound()
    {
        var input = new ParsedAddress("Buyer", "100 Main St", "Apt 99Z", "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "100 MAIN ST",
                SecondaryAddress = "APT 99Z",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "S"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.UnitNotFound, classified.Status);
        Assert.Equal("Unit not found by USPS", classified.Alert);
    }

    [Fact]
    public void Classifier_treats_formatting_and_zip_plus_4_as_Ok_with_no_alert()
    {
        var input = new ParsedAddress("Jane Doe", "123 north main street", "apartment 4", "springfield", "il", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "123 N MAIN ST",
                SecondaryAddress = "APT 4",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                ZIPPlus4 = "1234",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.Ok, classified.Status);
        Assert.Null(classified.Alert);
        Assert.False(classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_treats_street_number_change_as_SuggestedAddress()
    {
        var input = new ParsedAddress("Buyer", "124 Main St", null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "123 MAIN ST",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.SuggestedAddress, classified.Status);
        Assert.Contains("USPS suggests", classified.Alert);
        Assert.True(classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_treats_5_digit_zip_change_as_SuggestedAddress()
    {
        var input = new ParsedAddress("Buyer", "100 Beverly Hills Dr", null, "Beverly Hills", "CA", "90210", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "100 BEVERLY HILLS DR",
                City = "BEVERLY HILLS",
                State = "CA",
                ZIPCode = "90211",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.SuggestedAddress, classified.Status);
        Assert.True(classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_skips_non_US_orders()
    {
        var input = new ParsedAddress("Foreign Buyer", "10 Downing St", null, "London", "ENG", "SW1A 2AA", "GB", false);
        var uspsResult = new UspsAddressCheckResult(false, 0, null, null, null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.SkippedNonUs, classified.Status);
        Assert.Null(classified.Alert);
    }

    [Fact]
    public async Task VerifyOrderAsync_updates_order_entity_with_check_results()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        settings.Settings[SettingKeys.UspsConsumerKey] = "test_key";
        settings.Settings[SettingKeys.UspsConsumerSecret] = "test_secret";

        var (uspsClient, _) = CreateUspsClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":28800}""");

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "500 ROCK WAY",
                "secondaryAddress": null,
                "city": "PHOENIX",
                "state": "AZ",
                "ZIPCode": "85001",
                "ZIPPlus4": "1234",
                "DPVConfirmation": "D"
            }
            """);
        });

        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);

        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 55555,
                BuyerName = "Alice Cooper",
                IsPaid = true,
                Status = "paid",
                RawJson = """
                {
                    "name": "Alice Cooper",
                    "first_line": "500 Rock Way",
                    "city": "Phoenix",
                    "state": "AZ",
                    "zip": "85001",
                    "country_iso": "US"
                }
                """
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        await service.VerifyOrderAsync(orderId);

        await using (var db = testDb.CreateDbContext())
        {
            var verified = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(AddressCheckStatuses.MissingUnit, verified.AddressCheckStatus);
            Assert.Equal("Apartment or unit missing", verified.AddressCheckAlert);
            Assert.NotNull(verified.AddressCheckHash);
            Assert.NotNull(verified.AddressCheckedAtUtc);
            Assert.NotNull(verified.AddressCheckSuggested);
            Assert.True(verified.HasActiveAddressAlert);
        }
    }

    [Fact]
    public async Task ApplyUspsSuggestedAddress_updates_override_and_queues_recheck()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        var (uspsClient, _) = CreateUspsClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);

        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 77777,
                BuyerName = "Bob Smith",
                IsPaid = true,
                Status = "paid",
                AddressCheckSuggested = "Bob Smith\n123 MAIN ST\nAPT 4B\nSPRINGFIELD, IL 62704-1234"
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        var success = await service.ApplyUspsSuggestedAddressAsync(orderId, "AdminUser");
        Assert.True(success);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal("Bob Smith\n123 MAIN ST\nAPT 4B\nSPRINGFIELD, IL 62704-1234", order.AddressOverride);
        }

        Assert.Single(jobQueue.Queued);
        Assert.Equal(VerifyAddressJob.Type, jobQueue.Queued[0].JobType);
        Assert.Single(audit.Entries, e => e.Action == "order.address.use-usps");
    }

    [Fact]
    public async Task DismissAlert_dismisses_alert_until_address_changes()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        var (uspsClient, _) = CreateUspsClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);

        int orderId;
        const string initialHash = "initial_hash_123";

        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 88888,
                BuyerName = "Charlie Brown",
                IsPaid = true,
                Status = "paid",
                AddressCheckStatus = AddressCheckStatuses.MissingUnit,
                AddressCheckAlert = "Apartment or unit missing",
                AddressCheckHash = initialHash
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;

            Assert.True(order.HasActiveAddressAlert);
        }

        // Dismiss alert
        var dismissed = await service.DismissAlertAsync(orderId, "AdminUser");
        Assert.True(dismissed);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(initialHash, order.AddressCheckDismissedHash);
            Assert.False(order.HasActiveAddressAlert); // Now dismissed!

            // When address changes later, AddressCheckHash updates
            order.AddressCheckHash = "new_changed_address_hash_456";
            Assert.True(order.HasActiveAddressAlert); // Reactivated because hash changed!
        }

        Assert.Single(audit.Entries, e => e.Action == "order.address.dismiss");
    }

    [Fact]
    public async Task OrderReceived_handler_queues_check_only_when_enabled()
    {
        var settings = new InMemorySettingsStore();
        var queue = new InMemoryJobQueue();
        var service = new AddressVerificationService(
            new TestDb(), settings,
            CreateUspsClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)).Client,
            queue, new InMemoryAuditLog(), new ImmediateTimeProvider(),
            NullLogger<AddressVerificationService>.Instance);

        var handler = new OrderReceivedAddressVerificationHandler(settings, service);

        // 1. When disabled
        settings.Settings[SettingKeys.UspsEnabled] = "0";
        await handler.HandleAsync(new OrderReceived(1, 100, 200), CancellationToken.None);
        Assert.Empty(queue.Queued);

        // 2. When enabled
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        await handler.HandleAsync(new OrderReceived(1, 100, 200), CancellationToken.None);
        Assert.Single(queue.Queued);
        Assert.Equal(VerifyAddressJob.Type, queue.Queued[0].JobType);
    }

    [Fact]
    public void HaPayload_includes_address_alert_and_css_when_active()
    {
        var orderWithAlert = new Order
        {
            Id = 1,
            ShopId = 1,
            EtsyReceiptId = 1001,
            BuyerName = "Jane Alert",
            IsPaid = true,
            Status = "paid",
            AddressCheckStatus = AddressCheckStatuses.MissingUnit,
            AddressCheckAlert = "Apartment or unit missing",
            AddressCheckHash = "hash1",
            AddressCheckSuggested = "Jane Alert\n123 MAIN ST\nAPT 2B\nSPRINGFIELD, IL 62704"
        };

        var orderValid = new Order
        {
            Id = 2,
            ShopId = 1,
            EtsyReceiptId = 1002,
            BuyerName = "Bob Ok",
            IsPaid = true,
            Status = "paid",
            AddressCheckStatus = AddressCheckStatuses.Ok,
            AddressCheckAlert = null,
            AddressCheckHash = "hash2"
        };

        var payload = HaPayloadBuilder.OpenOrders([orderWithAlert, orderValid], TimeZoneInfo.Utc);

        // 1. Top-level address_alerts array
        var alerts = payload["address_alerts"]!.AsArray();
        Assert.Single(alerts);
        var firstAlert = alerts[0]!.AsObject();
        Assert.Equal("1001", (string?)firstAlert["receipt_id"]);
        Assert.Equal("Jane Alert", (string?)firstAlert["buyer_name"]);
        Assert.Equal("MissingUnit", (string?)firstAlert["status"]);
        Assert.Equal("Apartment or unit missing", (string?)firstAlert["message"]);

        // 2. Top-level address_alert_css
        Assert.NotNull((string?)payload["address_alert_css"]);
        Assert.Contains(".address-alerts-blurb", (string?)payload["address_alert_css"]);

        // 3. Per-order address_alert in open_orders
        var openOrders = payload["open_orders"]!.AsArray();
        var alertOrderSummary = openOrders.First(o => (string?)o!["receipt_id"] == "1001")!.AsObject();
        Assert.NotNull(alertOrderSummary["address_alert"]);
        Assert.Equal("error", (string?)alertOrderSummary["address_alert"]!["severity"]);

        var validOrderSummary = openOrders.First(o => (string?)o!["receipt_id"] == "1002")!.AsObject();
        Assert.Null(validOrderSummary["address_alert"]);
    }
}
