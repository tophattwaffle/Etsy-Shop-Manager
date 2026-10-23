using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Http;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ChallengerM11ApiSimulationAndStateTests
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

    private static ClaimsPrincipal AdminUser() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "tophattwaffle"), new Claim(ClaimTypes.Role, Roles.Admin)], "test"));

    private sealed record TestHarness(ServiceProvider Provider, ScriptedHandler Http, InMemoryTokenStore Store,
        RecordingPublisher Events, ImmediateTimeProvider Time)
    {
        public EtsyApiClient Api => Provider.GetRequiredService<EtsyApiClient>();
        public EtsyTokenService Tokens => Provider.GetRequiredService<EtsyTokenService>();
    }

    private static TestHarness BuildHarness(Func<HttpRequestMessage, int, HttpResponseMessage> respond, int maxRetries = 3)
    {
        var http = new ScriptedHandler(respond);
        var store = new InMemoryTokenStore();
        var events = new RecordingPublisher();
        var time = new ImmediateTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IEtsyCredentialsProvider, FakeCredentials>();
        services.AddSingleton<IEtsyTokenStore>(store);
        services.AddSingleton<IDomainEventPublisher>(events);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Etsy:MaxRetries"] = maxRetries.ToString(),
        }).Build();
        services.AddEtsy(config);
        services.AddHttpClient(EtsyOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => http);

        var sp = services.BuildServiceProvider();
        store.Tokens[1] = new EtsyTokenSet
        {
            AccessToken = "111.test-access",
            RefreshToken = "111.test-refresh",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            RefreshTokenIssuedAtUtc = DateTime.UtcNow,
            Scopes = "listings_w",
        };

        return new TestHarness(sp, http, store, events, time);
    }

    #region Area 1: FakeEtsyShop readiness-state endpoints (POST and PUT)

    [Fact]
    public async Task FakeEtsyShop_POST_readiness_state_creates_days_profile_and_updates_mutable_collection()
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();
        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "1"),
            new("max_processing_time", "2"),
            new("processing_time_unit", "days"),
        };

        var response = await client.CreateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, fields, auth);
        var created = response.Value;

        Assert.NotNull(created);
        Assert.True(created.ReadinessStateId > 0);
        Assert.Equal("ready_to_ship", created.ReadinessState);
        Assert.Equal(1, created.MinProcessingDays);
        Assert.Equal(2, created.MaxProcessingDays);
        Assert.Equal("1-2 business days", created.ProcessingDaysDisplayLabel);

        // Verify write recorded in shop.Writes
        var write = Assert.Single(shop.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions", write.Path);
        Assert.Contains("readiness_state=ready_to_ship", write.Body);
        Assert.Contains("min_processing_time=1", write.Body);
        Assert.Contains("max_processing_time=2", write.Body);
        Assert.Contains("processing_time_unit=days", write.Body);

        // Verify mutable collection has been updated
        var inMemory = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId);
        var found = inMemory.FirstOrDefault(p => (long)p["readiness_state_id"]! == created.ReadinessStateId);
        Assert.NotNull(found);
        Assert.Equal("ready_to_ship", (string?)found["readiness_state"]);
        Assert.Equal(1, (int)found["min_processing_days"]!);
        Assert.Equal(2, (int)found["max_processing_days"]!);
        Assert.Equal("1-2 business days", (string?)found["processing_days_display_label"]);

        // Verify GET returns the new profile
        using var http = new HttpClient(shop.Api) { BaseAddress = new Uri("https://api.etsy.com") };
        var getResp = await http.GetAsync($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var content = await getResp.Content.ReadAsStringAsync();
        Assert.Contains(created.ReadinessStateId.ToString(), content);
    }

    [Theory]
    [InlineData("weeks", 2, 3, 14, 21, "2-3 weeks")]
    [InlineData("weeks", 1, 1, 7, 7, "1 week")]
    [InlineData("days", 1, 1, 1, 1, "1 business day")]
    [InlineData("days", 3, 5, 3, 5, "3-5 business days")]
    public async Task FakeEtsyShop_POST_readiness_state_calculates_days_and_formats_display_label(
        string unit, int minTime, int maxTime, int expectedMinDays, int expectedMaxDays, string expectedLabel)
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();
        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "made_to_order"),
            new("min_processing_time", minTime.ToString()),
            new("max_processing_time", maxTime.ToString()),
            new("processing_time_unit", unit),
        };

        var response = await client.CreateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, fields, auth);
        var created = response.Value;

        Assert.Equal(expectedMinDays, created.MinProcessingDays);
        Assert.Equal(expectedMaxDays, created.MaxProcessingDays);
        Assert.Equal(expectedLabel, created.ProcessingDaysDisplayLabel);

        var inMemory = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId)
            .Single(p => (long)p["readiness_state_id"]! == created.ReadinessStateId);
        Assert.Equal(expectedMinDays, (int)inMemory["min_processing_days"]!);
        Assert.Equal(expectedMaxDays, (int)inMemory["max_processing_days"]!);
        Assert.Equal(expectedLabel, (string?)inMemory["processing_days_display_label"]);
    }

    [Fact]
    public async Task FakeEtsyShop_PUT_readiness_state_updates_existing_profile_in_place_and_records_write()
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();
        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);

        // Profile 601 exists initially in FakeEtsyShop
        var initial = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId).Single(p => (long)p["readiness_state_id"]! == 601);
        Assert.Equal("made_to_order", (string?)initial["readiness_state"]);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "2"),
            new("max_processing_time", "4"),
            new("processing_time_unit", "weeks"),
        };

        var response = await client.UpdateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, 601, fields, auth);
        var updated = response.Value;

        Assert.Equal(601, updated.ReadinessStateId);
        Assert.Equal("ready_to_ship", updated.ReadinessState);
        Assert.Equal(14, updated.MinProcessingDays);
        Assert.Equal(28, updated.MaxProcessingDays);
        Assert.Equal("2-4 weeks", updated.ProcessingDaysDisplayLabel);

        var write = Assert.Single(shop.Writes);
        Assert.Equal("PUT", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions/601", write.Path);
        Assert.Contains("readiness_state=ready_to_ship", write.Body);
        Assert.Contains("min_processing_time=2", write.Body);
        Assert.Contains("max_processing_time=4", write.Body);
        Assert.Contains("processing_time_unit=weeks", write.Body);

        // Verify updated in-place in mutable collection
        var updatedInMemory = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId).Single(p => (long)p["readiness_state_id"]! == 601);
        Assert.Equal("ready_to_ship", (string?)updatedInMemory["readiness_state"]);
        Assert.Equal(14, (int)updatedInMemory["min_processing_days"]!);
        Assert.Equal(28, (int)updatedInMemory["max_processing_days"]!);
        Assert.Equal("2-4 weeks", (string?)updatedInMemory["processing_days_display_label"]);
    }

    [Fact]
    public async Task FakeEtsyShop_PUT_nonexistent_profile_returns_404_not_found()
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();
        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "1"),
            new("max_processing_time", "1"),
        };

        var ex = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.UpdateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, 999999, fields, auth));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);

        var write = Assert.Single(shop.Writes);
        Assert.Equal("PUT", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/readiness-state-definitions/999999", write.Path);
    }

    [Fact]
    public async Task FakeEtsyShop_POST_and_PUT_fail_when_FailWritesWith_is_set_without_corrupting_state()
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();
        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);

        var countBefore = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId).Count;
        shop.FailWritesWith = HttpStatusCode.InternalServerError;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "1"),
            new("max_processing_time", "2"),
        };

        // POST failure
        var postEx = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.CreateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, fields, auth));
        Assert.Equal(HttpStatusCode.InternalServerError, postEx.StatusCode);
        Assert.Equal(countBefore, shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId).Count);

        // PUT failure
        var putEx = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.UpdateProcessingProfileAsync(FakeEtsyShop.EtsyShopId, 601, fields, auth));
        Assert.Equal(HttpStatusCode.InternalServerError, putEx.StatusCode);

        // Profile 601 in memory is unchanged
        var profile601 = shop.ProcessingProfilesFor(FakeEtsyShop.EtsyShopId).Single(p => (long)p["readiness_state_id"]! == 601);
        Assert.Equal("made_to_order", (string?)profile601["readiness_state"]);
    }

    #endregion

    #region Area 2: Idempotency & NoRetry Verification

    [Fact]
    public async Task CreateProcessingProfile_sets_NoRetry_preventing_retries_on_failure()
    {
        int postAttempts = 0;
        HttpRequestOptions? capturedOptions = null;

        var harness = BuildHarness((req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("readiness-state-definitions"))
            {
                postAttempts++;
                capturedOptions = req.Options;
                // Return 503 ServiceUnavailable or 429 TooManyRequests
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, maxRetries: 3);

        var auth = EtsyAuth.ForShop(1);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "made_to_order"),
            new("min_processing_time", "1"),
            new("max_processing_time", "2"),
        };

        // Call CreateProcessingProfileAsync: must fail with EtsyApiException
        await Assert.ThrowsAsync<EtsyApiException>(() =>
            harness.Api.CreateProcessingProfileAsync(42, fields, auth));

        // Empirically verify that NoRetry was set on HttpRequestMessage options
        Assert.NotNull(capturedOptions);
        var hasNoRetry = capturedOptions.TryGetValue(EtsyRetryHandler.NoRetry, out var noRetryVal);
        Assert.True(hasNoRetry, "EtsyRetryHandler.NoRetry key must be set in HttpRequestMessage.Options");
        Assert.True(noRetryVal, "NoRetry value must be true");

        // Empirically verify that only 1 attempt was made (zero retries), preventing double-creation!
        Assert.Equal(1, postAttempts);
    }

    [Fact]
    public async Task UpdateProcessingProfile_does_not_set_NoRetry_allowing_idempotent_retries()
    {
        int putAttempts = 0;
        var harness = BuildHarness((req, _) =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.Contains("readiness-state-definitions"))
            {
                putAttempts++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, maxRetries: 3);

        var auth = EtsyAuth.ForShop(1);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "1"),
            new("max_processing_time", "2"),
        };

        await Assert.ThrowsAsync<EtsyApiException>(() =>
            harness.Api.UpdateProcessingProfileAsync(42, 601, fields, auth));

        // PUT is idempotent and does not have NoRetry set, so it retries up to MaxRetries (1 initial + 3 retries = 4)
        Assert.Equal(4, putAttempts);
    }

    #endregion

    #region Area 3: Cross-shop isolation

    [Fact]
    public async Task FakeEtsyShop_readiness_state_collections_are_strictly_isolated_between_shops()
    {
        using var shop = new FakeEtsyShop();
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();

        const long Shop1EtsyId = FakeEtsyShop.EtsyShopId; // 42
        const long Shop2EtsyId = 43L;

        var auth1 = EtsyAuth.ForShop(FakeEtsyShop.ShopId);
        var auth2 = EtsyAuth.WithToken("shop2_token");

        // Initially Shop 1 has 1 profile (601), Shop 2 has none
        Assert.Single(shop.ProcessingProfilesFor(Shop1EtsyId));
        Assert.Empty(shop.ProcessingProfilesFor(Shop2EtsyId));

        // Create a profile for Shop 2
        var createFields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "ready_to_ship"),
            new("min_processing_time", "5"),
            new("max_processing_time", "5"),
            new("processing_time_unit", "days"),
        };

        var shop2ProfileRaw = await client.CreateProcessingProfileAsync(Shop2EtsyId, createFields, auth2);
        var shop2Profile = shop2ProfileRaw.Value;

        // Verify Shop 2 has 1 profile, Shop 1 still has 1 profile
        Assert.Single(shop.ProcessingProfilesFor(Shop1EtsyId));
        Assert.Single(shop.ProcessingProfilesFor(Shop2EtsyId));
        Assert.Equal(601, (long)shop.ProcessingProfilesFor(Shop1EtsyId)[0]["readiness_state_id"]!);
        Assert.Equal(shop2Profile.ReadinessStateId, (long)shop.ProcessingProfilesFor(Shop2EtsyId)[0]["readiness_state_id"]!);

        // Attempting to PUT Shop 2's profile using Shop 1's Etsy Shop ID must return 404
        var updateFields = new List<KeyValuePair<string, string>>
        {
            new("readiness_state", "made_to_order"),
            new("min_processing_time", "1"),
            new("max_processing_time", "1"),
        };

        var ex = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.UpdateProcessingProfileAsync(Shop1EtsyId, shop2Profile.ReadinessStateId, updateFields, auth1));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);

        // Verify Shop 2's profile was not modified by the cross-shop PUT attempt
        var shop2InMemory = shop.ProcessingProfilesFor(Shop2EtsyId).Single();
        Assert.Equal("ready_to_ship", (string?)shop2InMemory["readiness_state"]);
    }

    [Fact]
    public async Task ProcessingProfileService_UpdateAsync_rejects_updating_profile_belonging_to_another_shop()
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
            // Profile 701 belongs to Shop 2
            db.ProcessingProfiles.Add(new ProcessingProfile
            {
                ShopId = 2,
                ReadinessStateId = 701,
                ReadinessState = "made_to_order",
                MinProcessingDays = 2,
                MaxProcessingDays = 4,
            });
            await db.SaveChangesAsync();
        }

        var updateModel = new UpdateProcessingProfileModel
        {
            ReadinessState = "ready_to_ship",
            MinProcessingTime = 1,
            MaxProcessingTime = 1,
        };

        // Try to update Shop 2's profile (701) passing Shop 1's ID
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(FakeEtsyShop.ShopId, 701, updateModel, "tophattwaffle"));

        Assert.Contains("does not belong to shop", ex.Message);
        Assert.Empty(shop.Writes);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task ProcessingProfileService_BulkApply_strictly_skips_listings_belonging_to_another_shop()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Shop 1 Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int shop1ListingId;
        int shop2ListingId;

        await using (var db = shop.Db.CreateDbContext())
        {
            shop1ListingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;

            // Setup Shop 2
            var s2 = new Shop
            {
                Id = 2,
                EtsyShopId = 43,
                EtsyUserId = 222,
                Name = "SecondShop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow,
                Token = new ShopToken { EncryptedAccessToken = "tok_access", EncryptedRefreshToken = "tok_refresh", Scopes = "listings_w" },
            };
            db.Shops.Add(s2);

            // Add Profile 602 to Shop 1
            db.ProcessingProfiles.Add(new ProcessingProfile
            {
                ShopId = FakeEtsyShop.ShopId,
                ReadinessStateId = 602,
                ReadinessState = "ready_to_ship",
                MinProcessingDays = 1,
                MaxProcessingDays = 1,
                DisplayLabel = "1 day"
            });

            // Create listing in Shop 2
            var inv = new JsonObject
            {
                ["title"] = "Shop 2 Vase",
                ["inventory"] = new JsonObject
                {
                    ["products"] = new JsonArray(new JsonObject
                    {
                        ["product_id"] = 200101,
                        ["offerings"] = new JsonArray(new JsonObject
                        {
                            ["offering_id"] = 2001011,
                            ["price_amount"] = 2500,
                            ["readiness_state_id"] = 701L,
                        })
                    })
                }
            }.ToJsonString();

            var l2 = new Listing
            {
                ShopId = 2,
                EtsyListingId = 2001,
                Title = "Shop 2 Vase",
                Description = "Description",
                State = "active",
                CurrencyCode = "USD",
                PriceAmount = 2500,
                PriceDivisor = 100,
                Quantity = 5,
                EtsyState = new ListingEtsyState
                {
                    ManagedJson = inv,
                    BaseJson = "{}",
                    ManagedHash = "abc",
                    ManagedVersion = ManagedListingState.CurrentVersion,
                    FetchedAtUtc = DateTime.UtcNow,
                    ManagedChangedAtUtc = DateTime.UtcNow,
                }
            };
            db.Listings.Add(l2);
            await db.SaveChangesAsync();
            shop2ListingId = l2.Id;
        }

        var audit = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            audit);

        // Preview applying Shop 1's profile 602 to both listings
        var preview = await service.PreviewApplyAsync(FakeEtsyShop.ShopId, 602, [shop1ListingId, shop2ListingId], CancellationToken.None);
        Assert.Equal(2, preview.Count);

        var rowShop1 = preview.Single(r => r.ListingId == shop1ListingId);
        Assert.True(rowShop1.Changes);
        Assert.Null(rowShop1.Skipped);

        var rowShop2 = preview.Single(r => r.ListingId == shop2ListingId);
        Assert.False(rowShop2.Changes);
        Assert.Equal("Target profile belongs to another shop.", rowShop2.Skipped);

        // Apply
        var changed = await service.ApplyAsync(FakeEtsyShop.ShopId, 602, [shop1ListingId, shop2ListingId], "tophattwaffle", CancellationToken.None);
        Assert.Equal([shop1ListingId], changed);

        // Verify Shop 2's listing was NOT modified
        await using (var db = shop.Db.CreateDbContext())
        {
            var l2Check = await db.Listings.SingleAsync(l => l.Id == shop2ListingId);
            Assert.False(l2Check.HasDraft);
            Assert.Null(l2Check.DraftJson);
        }
    }

    [Fact]
    public async Task BulkListingEditor_ProcessingProfile_rejects_applying_profile_from_wrong_shop()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).Id;

            // Setup Shop 2 and profile 701 on Shop 2
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
                ReadinessStateId = 701,
                ReadinessState = "ready_to_ship",
                MinProcessingDays = 1,
                MaxProcessingDays = 2,
                DisplayLabel = "Shop 2 Profile"
            });
            await db.SaveChangesAsync();
        }

        var bulk = shop.Provider.GetRequiredService<BulkListingEditor>();

        // Attempt to apply Shop 2's profile (701) to Shop 1's listing
        var edit = new BulkEdit(BulkEditKind.ProcessingProfile,
            PerShop: new Dictionary<int, long?> { [FakeEtsyShop.ShopId] = 701L });

        var preview = await bulk.PreviewAsync([listingId], edit);
        var row = Assert.Single(preview);
        Assert.False(row.Changes);
        Assert.Equal("That choice isn't one of this shop's.", row.Skipped);

        // Apply must skip it
        var changed = await bulk.ApplyAsync([listingId], edit, AdminUser());
        Assert.Empty(changed);

        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.SingleAsync(l => l.Id == listingId);
            Assert.False(l.HasDraft);
        }
    }

    #endregion
}
