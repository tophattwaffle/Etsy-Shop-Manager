using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Usps;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM1WorkflowStressTests
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

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EtsyShopManager.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (EtsyShopManager.slnx) not found.");
    }

    private static string FindHaCardDirectory()
    {
        var root = FindRepoRoot();
        var candidate1 = Path.GetFullPath(Path.Combine(root, "..", "Etsy-Shop-Manager-HA-Card"));
        if (Directory.Exists(candidate1) && File.Exists(Path.Combine(candidate1, "etsy-orders-card.js")))
            return candidate1;

        const string candidate2 = @"C:\Projects\Etsy-Shop-Manager-HA-Card";
        if (Directory.Exists(candidate2) && File.Exists(Path.Combine(candidate2, "etsy-orders-card.js")))
            return candidate2;

        throw new DirectoryNotFoundException($"Could not locate Etsy-Shop-Manager-HA-Card directory. Checked {candidate1} and {candidate2}");
    }

    #region Stress Test Area 1: Order Card Dismissal Lifecycle

    [Fact]
    public async Task DismissalLifecycle_DismissingAlertForHashH1_SuppressesAlert_UntilAddressChangeYieldsHashH2()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        settings.Settings[SettingKeys.UspsConsumerKey] = "key";
        settings.Settings[SettingKeys.UspsConsumerSecret] = "secret";

        var (uspsClient, _) = CreateUspsClient((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":28800}""");

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "500 ROCK WAY",
                "secondaryAddress": null,
                "city": "PHOENIX",
                "state": "AZ",
                "ZIPCode": "85001",
                "DPVConfirmation": "D"
            }
            """);
        });

        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);
        var overrideService = new OrderOverrideService(testDb, audit, service);

        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 91001,
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

        // 1. Initial verification produces alert and hash H1
        await service.VerifyOrderAsync(orderId);

        string hashH1;
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(AddressCheckStatuses.MissingUnit, order.AddressCheckStatus);
            Assert.Equal("Apartment or unit missing", order.AddressCheckAlert);
            Assert.NotNull(order.AddressCheckHash);
            Assert.Null(order.AddressCheckDismissedHash);
            Assert.True(order.HasActiveAddressAlert);
            hashH1 = order.AddressCheckHash;
        }

        // 2. Dismiss alert for hash H1
        var dismissed = await service.DismissAlertAsync(orderId, "AdminUser");
        Assert.True(dismissed);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(hashH1, order.AddressCheckDismissedHash);
            Assert.False(order.HasActiveAddressAlert, "Alert should be suppressed when DismissedHash equals AddressCheckHash");
        }

        // 3. Subsequent modification of address (via OrderOverrideService) changes address
        var form = await overrideService.GetFormAsync(orderId);
        Assert.NotNull(form);
        form.ChangeAddress = true;
        form.Address = "Alice Cooper\n777 Sunset Blvd\nLos Angeles, CA 90028";
        var saveErr = await overrideService.SaveAsync(form, "AdminUser");
        Assert.Null(saveErr);

        // Verify that address change queued a recheck
        Assert.Contains(jobQueue.Queued, j => j.JobType == VerifyAddressJob.Type);

        // 4. Recheck executes with new address
        await service.VerifyOrderAsync(orderId);

        // 5. Verify that hash H2 is different from H1 and alert is RE-ENABLED
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            var hashH2 = order.AddressCheckHash;
            Assert.NotNull(hashH2);
            Assert.NotEqual(hashH1, hashH2);
            Assert.Equal(hashH1, order.AddressCheckDismissedHash); // Still remembers old dismissal H1
            Assert.True(order.HasActiveAddressAlert, "Alert must be re-enabled because new hash H2 does not match dismissed hash H1");
        }
    }

    [Fact]
    public async Task DismissalLifecycle_CosmeticModifications_PreserveHashH1_AndAlertRemainsSuppressed()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        settings.Settings[SettingKeys.UspsConsumerKey] = "key";
        settings.Settings[SettingKeys.UspsConsumerSecret] = "secret";

        var (uspsClient, _) = CreateUspsClient((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":28800}""");

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "100 MAIN ST",
                "city": "SPRINGFIELD",
                "state": "IL",
                "ZIPCode": "62704",
                "DPVConfirmation": "D"
            }
            """);
        });

        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);
        var overrideService = new OrderOverrideService(testDb, audit, service);

        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 91002,
                BuyerName = "Bob Builder",
                IsPaid = true,
                Status = "paid",
                RawJson = """
                {
                    "name": "Bob Builder",
                    "first_line": "100 Main St",
                    "city": "Springfield",
                    "state": "IL",
                    "zip": "62704",
                    "country_iso": "US"
                }
                """
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        // Verify and dismiss
        await service.VerifyOrderAsync(orderId);
        await service.DismissAlertAsync(orderId, "AdminUser");

        string initialHash;
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            initialHash = order.AddressCheckHash!;
            Assert.False(order.HasActiveAddressAlert);
        }

        // Apply cosmetic change (casing, punctuation, and ZIP+4): "100 MAIN ST." with "62704-1234"
        var form = await overrideService.GetFormAsync(orderId);
        Assert.NotNull(form);
        form.ChangeAddress = true;
        form.Address = "Bob Builder\n100 MAIN ST.\nSpringfield, IL 62704-1234";
        await overrideService.SaveAsync(form, "AdminUser");

        // Recheck with cosmetic change
        await service.VerifyOrderAsync(orderId);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(initialHash, order.AddressCheckHash);
            Assert.Equal(initialHash, order.AddressCheckDismissedHash);
            Assert.False(order.HasActiveAddressAlert, "Alert should remain suppressed across purely cosmetic changes that yield identical canonical hash");
        }
    }

    [Fact]
    public async Task DismissalLifecycle_RevertingToOriginalAddress_RestoresDismissalSuppression()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        settings.Settings[SettingKeys.UspsConsumerKey] = "key";
        settings.Settings[SettingKeys.UspsConsumerSecret] = "secret";

        var (uspsClient, _) = CreateUspsClient((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":28800}""");

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "100 MAIN ST",
                "city": "SPRINGFIELD",
                "state": "IL",
                "ZIPCode": "62704",
                "DPVConfirmation": "D"
            }
            """);
        });

        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);
        var overrideService = new OrderOverrideService(testDb, audit, service);

        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 91003,
                BuyerName = "Charlie Chaplin",
                IsPaid = true,
                Status = "paid",
                RawJson = """
                {
                    "name": "Charlie Chaplin",
                    "first_line": "100 Main St",
                    "city": "Springfield",
                    "state": "IL",
                    "zip": "62704",
                    "country_iso": "US"
                }
                """
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        // Verify and dismiss H1
        await service.VerifyOrderAsync(orderId);
        await service.DismissAlertAsync(orderId, "AdminUser");

        string hashH1;
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            hashH1 = order.AddressCheckHash!;
            Assert.False(order.HasActiveAddressAlert);
        }

        // Change to substantive address H2 -> re-enables alert
        var form = await overrideService.GetFormAsync(orderId);
        Assert.NotNull(form);
        form.ChangeAddress = true;
        form.Address = "Charlie Chaplin\n200 Other Blvd\nChicago, IL 60601";
        await overrideService.SaveAsync(form, "AdminUser");
        await service.VerifyOrderAsync(orderId);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.NotEqual(hashH1, order.AddressCheckHash);
            Assert.True(order.HasActiveAddressAlert);
        }

        // Revert back to original address H1 (clear address override)
        form = await overrideService.GetFormAsync(orderId);
        Assert.NotNull(form);
        form.ChangeAddress = false;
        await overrideService.SaveAsync(form, "AdminUser");
        await service.VerifyOrderAsync(orderId);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(hashH1, order.AddressCheckHash);
            Assert.Equal(hashH1, order.AddressCheckDismissedHash);
            Assert.False(order.HasActiveAddressAlert, "Alert should re-suppress when address is reverted to previously dismissed hash H1");
        }
    }

    #endregion

    #region Stress Test Area 2: "Use USPS's Version" Action

    [Fact]
    public async Task UseUspsVersion_StandardWorkflow_UpdatesOverride_PreservesLine0RecipientName_AndUpdatesHash()
    {
        using var testDb = new TestDb();
        var settings = new InMemorySettingsStore();
        settings.Settings[SettingKeys.UspsEnabled] = "1";
        settings.Settings[SettingKeys.UspsConsumerKey] = "key";
        settings.Settings[SettingKeys.UspsConsumerSecret] = "secret";

        var callCount = 0;
        var (uspsClient, _) = CreateUspsClient((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":28800}""");

            callCount++;
            if (callCount == 1)
            {
                // First call: initial check reports unit missing with suggested apartment
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                {
                    "streetAddress": "123 N MAIN ST",
                    "secondaryAddress": "APT 4B",
                    "city": "SPRINGFIELD",
                    "state": "IL",
                    "ZIPCode": "62704",
                    "ZIPPlus4": "1234",
                    "DPVConfirmation": "D"
                }
                """);
            }

            // Second call (after applying USPS suggestion): DPV confirmation Y and valid Ok
            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "123 N MAIN ST",
                "secondaryAddress": "APT 4B",
                "city": "SPRINGFIELD",
                "state": "IL",
                "ZIPCode": "62704",
                "ZIPPlus4": "1234",
                "DPVConfirmation": "Y"
            }
            """);
        });

        var jobQueue = new InMemoryJobQueue();
        var audit = new InMemoryAuditLog();
        var time = new ImmediateTimeProvider();
        var service = new AddressVerificationService(testDb, settings, uspsClient, jobQueue, audit, time, NullLogger<AddressVerificationService>.Instance);

        const string recipientName = "Dr. Jane H. Watson, Jr.";
        int orderId;
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { Id = 1, EtsyShopId = 1, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);

            var order = new Order
            {
                ShopId = 1,
                EtsyReceiptId = 92001,
                BuyerName = recipientName,
                IsPaid = true,
                Status = "paid",
                RawJson = $$"""
                {
                    "name": "{{recipientName}}",
                    "first_line": "123 Main St",
                    "city": "Springfield",
                    "state": "IL",
                    "zip": "62704",
                    "country_iso": "US"
                }
                """
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        // 1. Initial verification produces suggested address
        await service.VerifyOrderAsync(orderId);

        string originalHash;
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(AddressCheckStatuses.MissingUnit, order.AddressCheckStatus);
            Assert.NotNull(order.AddressCheckSuggested);
            originalHash = order.AddressCheckHash!;
        }

        // 2. Click "Use USPS's version"
        var applied = await service.ApplyUspsSuggestedAddressAsync(orderId, "AdminUser");
        Assert.True(applied);

        // 3. Verify AddressOverride was updated and preserves recipient name on Line 0
        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.NotNull(order.AddressOverride);

            var lines = order.AddressOverride.Split('\n', StringSplitOptions.TrimEntries);
            Assert.True(lines.Length >= 3, "Address override must contain at least 3 lines");
            Assert.Equal(recipientName, lines[0]); // STRICT LINE 0 PRESERVATION
            Assert.Equal("123 N MAIN ST", lines[1]);
            Assert.Equal("APT 4B", lines[2]);
            Assert.Equal("SPRINGFIELD, IL 62704-1234", lines[3]);

            // Verify UsAddressParser correctly parses the new override
            var parsed = UsAddressParser.Parse(order);
            Assert.Equal(recipientName, parsed.RecipientName);
            Assert.Equal("123 N MAIN ST", parsed.Street1);
            Assert.Equal("APT 4B", parsed.Street2);
            Assert.Equal("SPRINGFIELD", parsed.City);
            Assert.Equal("IL", parsed.State);
            Assert.Equal("62704-1234", parsed.Zip);

            // New address hash must differ from original because secondary address was added
            var newHash = UsAddressParser.ComputeAddressHash(parsed);
            Assert.NotEqual(originalHash, newHash);
        }

        // 4. Verify recheck job was queued and audit log was recorded
        Assert.Single(jobQueue.Queued);
        Assert.Equal(VerifyAddressJob.Type, jobQueue.Queued[0].JobType);
        Assert.Single(audit.Entries, e => e.Action == "order.address.use-usps");

        // 5. Execute recheck job and verify alert is resolved (Status = Ok, Alert = null, HasActiveAddressAlert = false)
        await service.VerifyOrderAsync(orderId);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(AddressCheckStatuses.Ok, order.AddressCheckStatus);
            Assert.Null(order.AddressCheckAlert);
            Assert.False(order.HasActiveAddressAlert);
            Assert.NotEqual(originalHash, order.AddressCheckHash);
        }
    }

    [Fact]
    public async Task UseUspsVersion_WhenNoSuggestedAddress_ReturnsFalseAndLeavesOverrideIntact()
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
                EtsyReceiptId = 92002,
                BuyerName = "No Suggestion Order",
                IsPaid = true,
                Status = "paid",
                AddressOverride = "Existing Override Line 1\nLine 2\nCity, ST 12345",
                AddressCheckSuggested = null // No suggestion available!
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        var result = await service.ApplyUspsSuggestedAddressAsync(orderId, "AdminUser");
        Assert.False(result);

        await using (var db = testDb.CreateDbContext())
        {
            var order = await db.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal("Existing Override Line 1\nLine 2\nCity, ST 12345", order.AddressOverride);
        }

        Assert.Empty(jobQueue.Queued);
        Assert.Empty(audit.Entries);
    }

    [Theory]
    [InlineData("Dr. Martin Luther King, Jr.")]
    [InlineData("O'Connor & Sons")]
    [InlineData("Jane Doe #2")]
    [InlineData("Smith-Jones (C/O Receiving Dept)")]
    public void FormatSuggestedAddress_PreservesSpecialRecipientNamesVerbatim(string recipientName)
    {
        var uspsResp = new UspsAddressResponse
        {
            StreetAddress = "100 CONGRESS AVE",
            SecondaryAddress = "STE 200",
            City = "AUSTIN",
            State = "TX",
            ZIPCode = "78701",
            ZIPPlus4 = "1234"
        };

        var formatted = AddressClassifier.FormatSuggestedAddress(recipientName, uspsResp);
        var lines = formatted.Split('\n');

        Assert.Equal(recipientName, lines[0]);
        Assert.Equal("100 CONGRESS AVE", lines[1]);
        Assert.Equal("STE 200", lines[2]);
        Assert.Equal("AUSTIN, TX 78701-1234", lines[3]);

        var parsed = UsAddressParser.ParseOverrideText(formatted);
        Assert.Equal(recipientName, parsed.RecipientName);
        Assert.Equal("100 CONGRESS AVE", parsed.Street1);
        Assert.Equal("STE 200", parsed.Street2);
        Assert.Equal("AUSTIN", parsed.City);
        Assert.Equal("TX", parsed.State);
        Assert.Equal("78701-1234", parsed.Zip);
        Assert.True(parsed.IsUs);
    }

    #endregion

    #region Stress Test Area 3: HaPayloadBuilder Backward Compatibility

    [Fact]
    public void HaPayloadBuilder_BackwardCompatibility_PreservesAllLegacyKeysAndTypes()
    {
        var orders = new List<Order>
        {
            new()
            {
                Id = 101,
                ShopId = 1,
                EtsyReceiptId = 80001,
                BuyerUserId = 12345,
                BuyerName = "Alice InChains",
                IsPaid = true,
                IsShipped = false,
                Status = "paid",
                CurrencyCode = "USD",
                EtsyCreatedAtUtc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
                AddressCheckStatus = AddressCheckStatuses.MissingUnit,
                AddressCheckAlert = "Apartment or unit missing",
                AddressCheckHash = "hash_alert_1",
                AddressCheckSuggested = "Alice InChains\n100 MAIN ST\nAPT 1\nSPRINGFIELD, IL 62704",
                Items =
                [
                    new OrderItem
                    {
                        Id = 1,
                        OrderId = 101,
                        EtsyTransactionId = 9001,
                        EtsyListingId = 501,
                        Title = "Handmade Wooden Widget",
                        Quantity = 2,
                        PriceAmount = 2500,
                        CurrencyCode = "USD",
                        EtsyCreatedAtUtc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
                        ExpectedShipAtUtc = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc),
                        Variations = [new OrderVariation { PropertyId = 1, ValueId = 11, Name = "Color", Value = "Red" }]
                    }
                ]
            },
            new()
            {
                Id = 102,
                ShopId = 1,
                EtsyReceiptId = 80002,
                BuyerUserId = 67890,
                BuyerName = "Bob Normal",
                IsPaid = true,
                IsShipped = false,
                Status = "paid",
                CurrencyCode = "USD",
                EtsyCreatedAtUtc = new DateTime(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc),
                AddressCheckStatus = AddressCheckStatuses.Ok,
                AddressCheckAlert = null,
                AddressCheckHash = "hash_ok_2",
                Items =
                [
                    new OrderItem
                    {
                        Id = 2,
                        OrderId = 102,
                        EtsyTransactionId = 9002,
                        EtsyListingId = 502,
                        Title = "Custom Brass Keyring",
                        Quantity = 1,
                        PriceAmount = 1500,
                        CurrencyCode = "USD",
                        EtsyCreatedAtUtc = new DateTime(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc),
                        ExpectedShipAtUtc = new DateTime(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc)
                    }
                ]
            }
        };

        var payload = HaPayloadBuilder.OpenOrders(orders, TimeZoneInfo.Utc);

        // 1. Verify JSON validity
        var jsonString = payload.ToJsonString();
        using var doc = JsonDocument.Parse(jsonString);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        // 2. Top-level contract verification
        Assert.True(payload.ContainsKey("open_orders_count"));
        Assert.Equal(2, (int)payload["open_orders_count"]!);

        Assert.True(payload.ContainsKey("total_items_needed"));
        Assert.Equal(3, (int)payload["total_items_needed"]!);

        Assert.True(payload.ContainsKey("items_needed"));
        var itemsNeeded = payload["items_needed"]!.AsArray();
        Assert.Equal(2, itemsNeeded.Count);

        Assert.True(payload.ContainsKey("open_orders"));
        var openOrders = payload["open_orders"]!.AsArray();
        Assert.Equal(2, openOrders.Count);

        // 3. New USPS address verification attributes
        Assert.True(payload.ContainsKey("address_alerts"));
        var addressAlerts = payload["address_alerts"]!.AsArray();
        Assert.Single(addressAlerts);

        Assert.True(payload.ContainsKey("address_alert_css"));
        var css = (string?)payload["address_alert_css"];
        Assert.NotNull(css);
        Assert.Contains(".address-alert", css);
        Assert.Contains(".address-alerts-blurb", css);

        // 4. Per-order summary verification for order with alert (80001)
        var alertSummary = openOrders.First(o => (string?)o!["receipt_id"] == "80001")!.AsObject();
        Assert.Equal("80001", (string?)alertSummary["receipt_id"]);
        Assert.Equal("12345", (string?)alertSummary["buyer_user_id"]);
        Assert.Equal(50.0, (double)alertSummary["order_total"]!);
        Assert.Equal("USD", (string?)alertSummary["currency_code"]);
        Assert.Equal(1, (int)alertSummary["item_count"]!);
        Assert.Equal("paid", (string?)alertSummary["status"]);
        Assert.True((bool)alertSummary["is_paid"]!);
        Assert.False((bool)alertSummary["is_shipped"]!);

        // Address alert on summary
        Assert.True(alertSummary.ContainsKey("address_alert"));
        var alertObj = alertSummary["address_alert"]!.AsObject();
        Assert.Equal("MissingUnit", (string?)alertObj["status"]);
        Assert.Equal("error", (string?)alertObj["severity"]);
        Assert.Equal("Apartment or unit missing", (string?)alertObj["message"]);
        Assert.False((bool)alertObj["is_suggestion"]!);
        Assert.Equal("Alice InChains\n100 MAIN ST\nAPT 1\nSPRINGFIELD, IL 62704", (string?)alertObj["suggested_address"]);

        // 5. Per-order summary verification for order without alert (80002)
        var normalSummary = openOrders.First(o => (string?)o!["receipt_id"] == "80002")!.AsObject();
        Assert.Equal("80002", (string?)normalSummary["receipt_id"]);
        // Crucial backward compatibility: address_alert key MUST NOT be present when there is no alert
        Assert.False(normalSummary.ContainsKey("address_alert"), "address_alert key must be omitted when order has no active alert");

        // 6. Transaction detail contract verification
        var normalItems = normalSummary["items"]!.AsArray();
        Assert.Single(normalItems);
        var item0 = normalItems[0]!.AsObject();
        Assert.Equal("9002", (string?)item0["transaction_id"]);
        Assert.Equal("80002", (string?)item0["receipt_id"]);
        Assert.Equal("Custom Brass Keyring", (string?)item0["title"]);
        Assert.Equal(1, (int)item0["quantity"]!);
        Assert.Equal(15.0, (double)item0["price_amount"]!);
        Assert.Equal("USD", (string?)item0["price_currency"]);
    }

    [Fact]
    public void HaPayloadBuilder_AddressAlert_SeverityDistinction_ErrorVsWarning()
    {
        var errorOrder = new Order
        {
            Id = 201,
            ShopId = 1,
            EtsyReceiptId = 88001,
            BuyerName = "Error Order",
            IsPaid = true,
            Status = "paid",
            AddressCheckStatus = AddressCheckStatuses.AddressNotFound,
            AddressCheckAlert = "Address not found by USPS",
            AddressCheckHash = "hash_err"
        };

        var warningOrder = new Order
        {
            Id = 202,
            ShopId = 1,
            EtsyReceiptId = 88002,
            BuyerName = "Warning Order",
            IsPaid = true,
            Status = "paid",
            AddressCheckStatus = AddressCheckStatuses.SuggestedAddress,
            AddressCheckAlert = "USPS suggests a change to this address",
            AddressCheckHash = "hash_warn",
            AddressCheckSuggested = "Warning Order\n100 MAIN ST\nSPRINGFIELD, IL 62704-1234"
        };

        var payload = HaPayloadBuilder.OpenOrders([errorOrder, warningOrder], TimeZoneInfo.Utc);
        var openOrders = payload["open_orders"]!.AsArray();

        var errorSummary = openOrders.First(o => (string?)o!["receipt_id"] == "88001")!["address_alert"]!.AsObject();
        Assert.Equal("error", (string?)errorSummary["severity"]);
        Assert.False((bool)errorSummary["is_suggestion"]!);

        var warnSummary = openOrders.First(o => (string?)o!["receipt_id"] == "88002")!["address_alert"]!.AsObject();
        Assert.Equal("warning", (string?)warnSummary["severity"]);
        Assert.True((bool)warnSummary["is_suggestion"]!);
    }

    [Fact]
    public void HaPayloadBuilder_DismissedAlert_IsExcludedFromPayload()
    {
        var dismissedOrder = new Order
        {
            Id = 301,
            ShopId = 1,
            EtsyReceiptId = 89001,
            BuyerName = "Dismissed Order",
            IsPaid = true,
            Status = "paid",
            AddressCheckStatus = AddressCheckStatuses.MissingUnit,
            AddressCheckAlert = "Apartment or unit missing",
            AddressCheckHash = "hash_dismissed_1",
            AddressCheckDismissedHash = "hash_dismissed_1" // Dismissed!
        };

        Assert.False(dismissedOrder.HasActiveAddressAlert);

        var payload = HaPayloadBuilder.OpenOrders([dismissedOrder], TimeZoneInfo.Utc);

        var alerts = payload["address_alerts"]!.AsArray();
        Assert.Empty(alerts);

        var summary = payload["open_orders"]!.AsArray()[0]!.AsObject();
        Assert.False(summary.ContainsKey("address_alert"), "Dismissed order must not have address_alert in receipt summary");
    }

    #endregion

    #region Stress Test Area 4: HA Card JS Syntax and Structure Execution

    [Fact]
    public void HaCardJs_NodeCheckSyntax_ExitsWithZero()
    {
        var haCardDir = FindHaCardDirectory();
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "--check etsy-orders-card.js",
            WorkingDirectory = haCardDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(error);
    }

    [Fact]
    public void HaCardJs_NodeExecution_VerifiesBlurbAndAlertRenderingAndXssSanitization()
    {
        var haCardDir = FindHaCardDirectory();
        const string testScript = """
        const fs = require('fs');
        const code = fs.readFileSync('etsy-orders-card.js', 'utf8');

        // Mock window, HTMLElement, customElements and browser globals
        global.window = global;
        global.HTMLElement = class {};
        global.customElements = { define: (name, cls) => { global[cls.name] = cls; } };
        global.navigator = { userAgent: 'test-agent' };

        eval(code);

        const card = new EtsyOrdersCard();

        // 1. Test _addressAlertsBlurb
        // Case A: 0 troubled orders
        if (card._addressAlertsBlurb([]) !== '') {
            console.error('Expected empty blurb for empty entries');
            process.exit(1);
        }
        if (card._addressAlertsBlurb([{ o: {} }]) !== '') {
            console.error('Expected empty blurb when no address_alert present');
            process.exit(2);
        }

        // Case B: 1 troubled order with XSS injection attempt in buyer name
        const single = card._addressAlertsBlurb([
            { o: { receipt_id: '1001', buyer_name: '<script>alert("xss")</script>', address_alert: { message: 'Missing unit' } } }
        ]);
        if (!single.includes('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;') || single.includes('<script>')) {
            console.error('XSS injection not sanitized in single order blurb');
            process.exit(3);
        }
        if (!single.includes('Address check issue on order for')) {
            console.error('Expected singular text in single order blurb');
            process.exit(4);
        }

        // Case C: Multiple troubled orders
        const multi = card._addressAlertsBlurb([
            { o: { receipt_id: '1001', buyer_name: 'Alice', address_alert: { message: 'Missing unit' } } },
            { o: { receipt_id: '1002', buyer_name: 'Bob', address_alert: { message: 'Address not found' } } }
        ]);
        if (!multi.includes('Address check issues on 2 orders: <b>Alice</b>, <b>Bob</b>')) {
            console.error('Expected plural text in multi order blurb:', multi);
            process.exit(5);
        }

        // 2. Test _orderAlert
        // Case A: No alert
        if (card._orderAlert({}) !== '') {
            console.error('Expected empty string when no alert on order');
            process.exit(6);
        }

        // Case B: Error alert (DPV N, D, S)
        const errHtml = card._orderAlert({
            address_alert: { message: 'Address not found by USPS', severity: 'error' }
        });
        if (!errHtml.includes('class="address-alert error"') || !errHtml.includes('alert-icon') || !errHtml.includes('Address not found by USPS')) {
            console.error('Error alert rendering failed:', errHtml);
            process.exit(7);
        }

        // Case C: Suggestion warning alert with multiline suggested address
        const warnHtml = card._orderAlert({
            address_alert: {
                message: 'USPS suggests a change',
                is_suggestion: true,
                severity: 'warning',
                suggested_address: 'Jane Doe\n100 MAIN ST\nSPRINGFIELD, IL 62704'
            }
        });
        if (!warnHtml.includes('class="address-alert suggestion"') || !warnHtml.includes('alert-icon') || !warnHtml.includes('100 MAIN ST, SPRINGFIELD, IL 62704')) {
            console.error('Warning suggestion alert rendering failed:', warnHtml);
            process.exit(8);
        }

        // Case D: XSS in message or suggested address
        const xssAlertHtml = card._orderAlert({
            address_alert: {
                message: '<img src=x onerror=alert(1)>',
                severity: 'error',
                suggested_address: '<b>Bold Street</b>'
            }
        });
        if (xssAlertHtml.includes('<img src=x') || xssAlertHtml.includes('<b>Bold Street</b>')) {
            console.error('XSS payload not escaped in order alert:', xssAlertHtml);
            process.exit(9);
        }

        // 3. Verify CSS rules exist in card code
        const requiredClasses = [
            '.address-alerts-blurb',
            '.address-alert',
            '.address-alert.error',
            '.address-alert.suggestion'
        ];
        for (const cls of requiredClasses) {
            if (!code.includes(cls)) {
                console.error('Missing required CSS class in card:', cls);
                process.exit(10);
            }
        }

        console.log('ALL NODE CARD JS TESTS PASSED');
        """;

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "-",
            WorkingDirectory = haCardDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process.StandardInput.Write(testScript);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        Assert.True(process.ExitCode == 0, $"Node script failed with exit code {process.ExitCode}. Error: {error}. Output: {output}");
        Assert.Contains("ALL NODE CARD JS TESTS PASSED", output);
    }

    #endregion
}
