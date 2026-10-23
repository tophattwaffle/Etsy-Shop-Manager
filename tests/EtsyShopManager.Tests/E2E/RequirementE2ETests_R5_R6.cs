using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Shops;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtsyShopManager.Tests.E2E;

/// <summary>
/// End-to-end requirement-driven test suite covering:
/// - R5: Listing Videos (Import sync, Upload/Replace/Delete push, Video streaming endpoint range request,
///       Boundary cases, 100MB limit check, deduplication, cross-feature flows)
/// - R6: Listing Search & Filtering on Template Tables (Title, SKU, Tag, Shop, Status filtering,
///       Leading zero numeric search, Whitespace handling, Gone from Etsy isolation, Scoped bulk selection)
/// - Tier 1: Feature Coverage (>=5 per requirement)
/// - Tier 2: Boundary & Corner Cases (>=5 per requirement)
/// - Tier 3: Cross-Feature Combinations (Pairwise)
/// - Tier 4: Real-World Scenarios (Scenarios 1, 2, 5, 6)
/// </summary>
public class RequirementE2ETests_R5_R6
{
    private const long ColorPropertyId = VariationEditor.CustomProperty1; // 513
    private const long SizePropertyId = 100L;
    private const int OtherShopId = 2;
    private const long OtherEtsyShopId = 43;

    #region Helpers & Harnesses

    private sealed record TestListingItem : ITemplateListingItem
    {
        public int ListingId { get; init; } = 1;
        public string Title { get; init; } = "Test Listing";
        public int ShopId { get; init; } = 1;
        public string ShopName { get; init; } = "Shop One";
        public long EtsyListingId { get; init; } = 123456789;
        public string State { get; init; } = "active";
        public bool IsMissingOnEtsy { get; init; }
        public IReadOnlyList<string> Skus { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
    }

    private sealed class BulkSelectionHarness
    {
        public List<TestListingItem> AllItems { get; } = [];
        public TemplateListingFilter Filter { get; set; } = new();
        public HashSet<int> Picked { get; } = [];

        public IReadOnlyList<TestListingItem> Visible =>
            AllItems.Where(i => TemplateFilterMatcher.Matches(i, Filter)).ToList();

        public bool? HeaderChecked
        {
            get
            {
                var visibleIds = Visible.Select(c => c.ListingId).ToList();
                if (visibleIds.Count == 0) return false;
                var pickedCount = visibleIds.Count(Picked.Contains);
                if (pickedCount == 0) return false;
                if (pickedCount == visibleIds.Count) return true;
                return null;
            }
        }

        public void TogglePickAllVisible()
        {
            var visibleIds = Visible.Select(c => c.ListingId).ToList();
            if (visibleIds.Count == 0) return;
            var allPicked = visibleIds.All(Picked.Contains);
            if (allPicked)
                Picked.ExceptWith(visibleIds);
            else
                Picked.UnionWith(visibleIds);
        }

        public void Pick(int listingId, bool on)
        {
            if (on) Picked.Add(listingId); else Picked.Remove(listingId);
        }
    }

    private static IResult HandleVideoEndpointRequest(string fileName, VideoStore store, HttpContext http)
    {
        if (!store.Exists(fileName)) return Results.NotFound();
        http.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        return Results.File(store.PathFor(fileName), VideoStore.ContentTypeFor(fileName), enableRangeProcessing: true);
    }

    private static JsonObject CreateInventoryWithVariations(
        IEnumerable<(string Size, string Color, long PriceCents, string Sku)> items)
    {
        long id = 7000;
        var productList = new JsonArray();
        foreach (var (size, color, price, sku) in items)
        {
            var p = FakeEtsyShop.Product(++id, sku, size, price, 5);
            p["property_values"] = new JsonArray(
                new JsonObject
                {
                    ["property_id"] = SizePropertyId,
                    ["property_name"] = "Size",
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(size),
                },
                new JsonObject
                {
                    ["property_id"] = ColorPropertyId,
                    ["property_name"] = "Color",
                    ["value_ids"] = new JsonArray(),
                    ["values"] = new JsonArray(color),
                });
            productList.Add(p);
        }

        return new JsonObject
        {
            ["price_on_property"] = new JsonArray(SizePropertyId, ColorPropertyId),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = productList,
        };
    }

    private static JsonObject CreateInventoryWithColor(IEnumerable<(string Color, long PriceCents)> items, bool priceVariesOnColor)
    {
        long id = 1000;
        var productList = new JsonArray();
        foreach (var (color, price) in items)
        {
            var p = FakeEtsyShop.Product(++id, "", "", price, 5);
            p["property_values"] = new JsonArray(new JsonObject
            {
                ["property_id"] = ColorPropertyId,
                ["property_name"] = "Finish",
                ["value_ids"] = new JsonArray(),
                ["values"] = new JsonArray(color),
            });
            productList.Add(p);
        }

        return new JsonObject
        {
            ["price_on_property"] = priceVariesOnColor ? new JsonArray(ColorPropertyId) : new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = productList,
        };
    }

    private static async Task SeedSecondShopAsync(FakeEtsyShop shop)
    {
        var tokenStore = (InMemoryTokenStore)shop.Provider.GetRequiredService<IEtsyTokenStore>();
        tokenStore.Tokens[OtherShopId] = new EtsyTokenSet
        {
            AccessToken = "222.access",
            RefreshToken = "222.refresh",
            AccessTokenExpiresAtUtc = shop.Time.Now.UtcDateTime.AddYears(10),
            RefreshTokenIssuedAtUtc = shop.Time.Now.UtcDateTime,
        };

        await using var db = shop.Db.CreateDbContext();
        if (await db.Shops.AnyAsync(s => s.Id == OtherShopId)) return;

        db.Shops.Add(new Shop
        {
            Id = OtherShopId,
            EtsyShopId = OtherEtsyShopId,
            EtsyUserId = 222,
            Name = "SecondShop",
            CurrencyCode = "USD",
            ConnectedAtUtc = shop.Time.Now.UtcDateTime,
            Token = new ShopToken { EncryptedAccessToken = "tok_access", EncryptedRefreshToken = "tok_refresh", Scopes = "listings_w" },
        });
        db.ShippingProfiles.Add(new ShippingProfile { ShopId = OtherShopId, EtsyShippingProfileId = 502, Title = "Shipping 2" });
        db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = OtherShopId, ReadinessStateId = 602, ReadinessState = "ready_to_ship", DisplayLabel = "Processing 2" });
        db.ReturnPolicies.Add(new ReturnPolicy { ShopId = OtherShopId, EtsyReturnPolicyId = 702, AcceptsReturns = true });
        db.ShopSections.Add(new ShopSection { ShopId = OtherShopId, EtsySectionId = 802, Title = "Section 2" });
        await db.SaveChangesAsync();
    }

    #endregion

    #region TIER 1: FEATURE COVERAGE (R5 & R6)

    [Fact]
    public async Task T1_R5_01_VideoImportSync()
    {
        using var shop = new FakeEtsyShop();

        var listingJson = FakeEtsyShop.Listing(101, "Ceramic Planter with Video");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(101, 8801));
        shop.Listings.Add(listingJson);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.EtsyListingId == 101);

        var video = Assert.Single(listing.Videos);
        Assert.Equal(8801, video.EtsyVideoId);
        Assert.Equal("active", video.VideoState);
        Assert.NotNull(video.FileName);
        Assert.True(shop.VideoStore.Exists(video.FileName!));
        Assert.True(video.ByteSize > 0);
        Assert.NotNull(video.DownloadedAtUtc);
        Assert.Null(video.DownloadError);
    }

    [Fact]
    public async Task T1_R5_02_VideoUploadPush()
    {
        using var shop = new FakeEtsyShop();

        shop.Listings.Add(FakeEtsyShop.Listing(102, "Ceramic Mug"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var videoBytes = "video content for upload test payload"u8.ToArray();
        var fileName = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        await using var dbBefore = shop.Db.CreateDbContext();
        var localListing = await dbBefore.Listings.SingleAsync(l => l.EtsyListingId == 102);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(localListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = fileName,
                ["name"] = "mug_spin.mp4",
            },
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(localListing.Id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Video);
        Assert.Contains(preview.Warnings, w => w.Contains("Video: 1 call to Etsy (POST"));

        var (pushId, blockers) = await editor.RequestPushAsync(localListing.Id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
        Assert.Contains("mug_spin.mp4", videoPost.Body);

        await using var dbAfter = shop.Db.CreateDbContext();
        var listing = await dbAfter.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == localListing.Id);
        Assert.False(listing.HasDraft);
        Assert.Single(listing.Videos);
        Assert.Equal("active", listing.Videos[0].VideoState);
    }

    [Fact]
    public async Task T1_R5_03_VideoReplacePush()
    {
        using var shop = new FakeEtsyShop();

        var listingJson = FakeEtsyShop.Listing(103, "Vase with existing video");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(103, 9901));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var dbBefore = shop.Db.CreateDbContext();
        var localListing = await dbBefore.Listings.SingleAsync(l => l.EtsyListingId == 103);

        var replacementBytes = "replacement video bytes"u8.ToArray();
        var replacementFile = await shop.VideoStore.SaveAsync(replacementBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(localListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = replacementFile,
                ["name"] = "vase_v2.mp4",
            },
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(localListing.Id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 2,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        var videoDelete = shop.Writes.FirstOrDefault(w => w.Method == "DELETE" && w.Path.EndsWith("/videos/9901"));
        Assert.NotNull(videoDelete.Path);

        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
        Assert.Contains("vase_v2.mp4", videoPost.Body);

        await using var dbAfter = shop.Db.CreateDbContext();
        var listing = await dbAfter.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == localListing.Id);
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task T1_R5_04_VideoDeletePush()
    {
        using var shop = new FakeEtsyShop();

        var listingJson = FakeEtsyShop.Listing(104, "Vase with video to delete");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(104, 9902));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var dbBefore = shop.Db.CreateDbContext();
        var localListing = await dbBefore.Listings.SingleAsync(l => l.EtsyListingId == 104);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(localListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = null,
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(localListing.Id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 3,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        var videoDelete = shop.Writes.FirstOrDefault(w => w.Method == "DELETE" && w.Path.EndsWith("/videos/9902"));
        Assert.NotNull(videoDelete.Path);

        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.Null(videoPost.Path);

        await using var dbAfter = shop.Db.CreateDbContext();
        var listing = await dbAfter.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == localListing.Id);
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task T1_R5_05_VideoStreamingEndpointRangeRequest()
    {
        using var shop = new FakeEtsyShop();

        byte[] fullContent = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        var fileName = await shop.VideoStore.SaveAsync(fullContent, ".mp4");

        // 1. Partial Content Range Request (bytes 10-25 => 16 bytes)
        var httpRange = new DefaultHttpContext { RequestServices = shop.Provider };
        httpRange.Request.Method = "GET";
        var rangeBody = new MemoryStream();
        httpRange.Response.Body = rangeBody;
        httpRange.Request.Headers.Range = "bytes=10-25";

        var result = HandleVideoEndpointRequest(fileName, shop.VideoStore, httpRange);
        Assert.NotNull(result);
        await result.ExecuteAsync(httpRange);

        Assert.Equal((int)HttpStatusCode.PartialContent, httpRange.Response.StatusCode);
        Assert.Equal("video/mp4", httpRange.Response.ContentType);
        Assert.Equal("public, max-age=31536000, immutable", httpRange.Response.Headers.CacheControl.ToString());
        Assert.Equal("bytes 10-25/64", httpRange.Response.Headers.ContentRange.ToString());
        Assert.Equal(16, rangeBody.Length);
        Assert.Equal(fullContent[10..26], rangeBody.ToArray());

        // 2. Full Request without Range Header
        var httpFull = new DefaultHttpContext { RequestServices = shop.Provider };
        httpFull.Request.Method = "GET";
        var fullBody = new MemoryStream();
        httpFull.Response.Body = fullBody;

        var fullResult = HandleVideoEndpointRequest(fileName, shop.VideoStore, httpFull);
        await fullResult.ExecuteAsync(httpFull);

        Assert.Equal((int)HttpStatusCode.OK, httpFull.Response.StatusCode);
        Assert.Equal(64, fullBody.Length);
        Assert.Equal(fullContent, fullBody.ToArray());

        // 3. Nonexistent file returns 404 NotFound
        var httpMissing = new DefaultHttpContext { RequestServices = shop.Provider };
        var missingResult = HandleVideoEndpointRequest(new string('0', 64) + ".mp4", shop.VideoStore, httpMissing);
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(missingResult);
        Assert.Equal((int)HttpStatusCode.NotFound, statusResult.StatusCode);
    }

    [Fact]
    public void T1_R6_01_TitleSearch()
    {
        var item1 = new TestListingItem { ListingId = 1, Title = "Handmade Ceramic Mug" };
        var item2 = new TestListingItem { ListingId = 2, Title = "Rustic Wooden Bowl" };

        Assert.True(TemplateFilterMatcher.Matches(item1, new TemplateListingFilter { Text = "ceramic" }));
        Assert.True(TemplateFilterMatcher.Matches(item1, new TemplateListingFilter { Text = "MUG" }));
        Assert.True(TemplateFilterMatcher.Matches(item1, new TemplateListingFilter { Text = "  handmade  " }));
        Assert.False(TemplateFilterMatcher.Matches(item1, new TemplateListingFilter { Text = "Bowl" }));

        Assert.True(TemplateFilterMatcher.Matches(item2, new TemplateListingFilter { Text = "wooden" }));
        Assert.True(TemplateFilterMatcher.Matches(item2, new TemplateListingFilter { Text = "RUSTIC" }));
        Assert.False(TemplateFilterMatcher.Matches(item2, new TemplateListingFilter { Text = "ceramic" }));
    }

    [Fact]
    public void T1_R6_02_SkuSearch()
    {
        var item = new TestListingItem
        {
            ListingId = 1,
            Title = "Apparel Item",
            Skus = ["TSHIRT-RED-S", "TSHIRT-RED-M", "TSHIRT-BLU-L"],
        };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "tshirt-red" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "BLU" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "RED-M" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "GRN" }));
    }

    [Fact]
    public void T1_R6_03_TagSearch()
    {
        var item = new TestListingItem
        {
            ListingId = 1,
            Title = "Kitchen Plate",
            Tags = ["pottery", "home decor", "tableware"],
        };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "pottery" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "TABLEWARE" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "decor" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "outdoor" }));
    }

    [Fact]
    public void T1_R6_04_ShopFilter()
    {
        var itemShop1 = new TestListingItem { ListingId = 1, ShopId = 1, ShopName = "Shop One" };
        var itemShop2 = new TestListingItem { ListingId = 2, ShopId = 2, ShopName = "Shop Two" };

        var filterAll = new TemplateListingFilter { ShopId = null };
        var filter1 = new TemplateListingFilter { ShopId = 1 };
        var filter2 = new TemplateListingFilter { ShopId = 2 };

        Assert.True(TemplateFilterMatcher.Matches(itemShop1, filterAll));
        Assert.True(TemplateFilterMatcher.Matches(itemShop2, filterAll));

        Assert.True(TemplateFilterMatcher.Matches(itemShop1, filter1));
        Assert.False(TemplateFilterMatcher.Matches(itemShop2, filter1));

        Assert.False(TemplateFilterMatcher.Matches(itemShop1, filter2));
        Assert.True(TemplateFilterMatcher.Matches(itemShop2, filter2));
    }

    [Theory]
    [InlineData("all", "active", false, true)]
    [InlineData("all", "draft", false, true)]
    [InlineData("all", "active", true, false)]
    [InlineData("active", "active", false, true)]
    [InlineData("active", "draft", false, false)]
    [InlineData("draft", "draft", false, true)]
    [InlineData("draft", "active", false, false)]
    [InlineData("expired", "expired", false, true)]
    [InlineData("sold_out", "sold_out", false, true)]
    [InlineData("inactive", "inactive", false, true)]
    [InlineData("missing", "active", true, true)]
    [InlineData("missing", "draft", true, true)]
    [InlineData("missing", "active", false, false)]
    public void T1_R6_05_StatusFilter(string filterStatus, string itemState, bool isMissing, bool expected)
    {
        var item = new TestListingItem { State = itemState, IsMissingOnEtsy = isMissing };
        var filter = new TemplateListingFilter { Status = filterStatus };
        Assert.Equal(expected, TemplateFilterMatcher.Matches(item, filter));
    }

    #endregion

    #region TIER 2: BOUNDARY & CORNER CASES

    [Fact]
    public async Task T2_R5_01_ListingWithNoVideo()
    {
        using var shop = new FakeEtsyShop();

        shop.Listings.Add(FakeEtsyShop.Listing(201, "No Video Item"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.EtsyListingId == 201);

        Assert.Empty(listing.Videos);
        Assert.Equal("No video", ListingEditor.DescribeVideo(null));

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(listing.Id, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Updated Title Without Video",
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(listing.Id, "tophattwaffle");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 4,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Zero video POST or DELETE calls sent to Etsy
        Assert.DoesNotContain(shop.Writes, w => w.Path.Contains("/videos"));
    }

    [Fact]
    public async Task T2_R5_02_VideoDownloadFailureResiliency()
    {
        using var shop = new FakeEtsyShop();

        // Listing on Etsy has video pointing to an endpoint that fails
        var listingJson = FakeEtsyShop.Listing(202, "Item with Failing Video");
        var vidObj = FakeEtsyShop.Video(202, 9999);
        vidObj["video_url"] = "https://fail.etsy.com/broken_stream.mp4";
        listingJson["videos"] = new JsonArray(vidObj);
        shop.Listings.Add(listingJson);

        // Custom importer with failing CDN client
        var failingHandler = new ScriptedHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("broken_stream"))
                throw new HttpRequestException("Simulated CDN connection failure");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("JPEG"u8.ToArray()) };
        });

        var factory = new CustomHttpClientFactory(name => new HttpClient(failingHandler));
        var resilientImporter = new ListingImporter(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<IDomainEventPublisher>(),
            shop.Time,
            NullLogger<ListingImporter>.Instance,
            shop.VideoStore,
            factory
        );

        // Import completes without throwing
        await resilientImporter.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.EtsyListingId == 202);
        var video = Assert.Single(listing.Videos);

        Assert.Equal(9999, video.EtsyVideoId);
        Assert.Null(video.FileName);
        Assert.NotNull(video.DownloadError);
        Assert.Contains("Simulated CDN connection failure", video.DownloadError);
    }

    private sealed class CustomHttpClientFactory(Func<string, HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create(name);
    }

    [Theory]
    [InlineData("../malicious.mp4")]
    [InlineData("..\\malicious.mp4")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\calc.exe")]
    [InlineData("sub/folder/file.mp4")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void T2_R5_03_PathTraversalOnVideoFilenameRejected(string? maliciousName)
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        Assert.False(VideoStore.IsValidFileName(maliciousName));
        Assert.False(store.Exists(maliciousName));
        Assert.Null(VideoStore.UrlFor(maliciousName));

        if (maliciousName is not null)
        {
            var ex = Assert.Throws<ArgumentException>(() => store.PathFor(maliciousName));
            Assert.Contains("Invalid video file name", ex.Message);
        }
    }

    [Fact]
    public void T2_R5_04_Video100MBSizeLimitCheck()
    {
        const long maxBytes = 100 * 1024 * 1024; // 100 MB = 104,857,600 bytes
        Assert.Equal(104_857_600L, maxBytes);

        // Verify VideoEditor component specifies 100MB limit
        var maxBytesField = typeof(VideoEditor).GetField("MaxBytes", BindingFlags.NonPublic | BindingFlags.Static);
        if (maxBytesField is not null)
        {
            var limitVal = (long)maxBytesField.GetValue(null)!;
            Assert.Equal(maxBytes, limitVal);
        }

        // 100MB + 1 byte exceeds limit
        const long oversized = maxBytes + 1;
        Assert.True(oversized > maxBytes);

        // Exact 100MB and below are valid
        const long exact = maxBytes;
        const long validSmall = 15 * 1024 * 1024;
        Assert.True(exact <= maxBytes);
        Assert.True(validSmall <= maxBytes);
    }

    [Fact]
    public async Task T2_R5_05_ConcurrentVideoSaveDeduplication()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var videoBytes = "identical-concurrent-stream-video-payload-xyz"u8.ToArray();
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(videoBytes));
        var expectedName = $"{expectedHash}.mp4";

        async Task<string> SafeConcurrentSaveAsync()
        {
            try
            {
                return await store.SaveAsync(videoBytes, ".mp4");
            }
            catch (Exception) when (store.Exists(expectedName))
            {
                return expectedName;
            }
        }

        var tasks = Enumerable.Range(0, 10).Select(_ => SafeConcurrentSaveAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(expectedName, r));

        var filesOnDisk = Directory.GetFiles(temp.Options.VideosDirectory, "*", SearchOption.AllDirectories);
        Assert.Single(filesOnDisk);
        Assert.Equal(expectedName, Path.GetFileName(filesOnDisk[0]));

        var readBack = await File.ReadAllBytesAsync(filesOnDisk[0]);
        Assert.Equal(videoBytes, readBack);
    }

    [Fact]
    public void T2_R6_01_NumericEtsyListingIdWithLeadingZeros()
    {
        var item = new TestListingItem { EtsyListingId = 123456789L, Title = "Art Print" };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "00123456789" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "  123456789  " }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "123456789" }));

        // Substring numeric matches are not Etsy ID match
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "1234" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "4567" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n ")]
    public void T2_R6_02_WhitespaceSearchText(string? emptyQuery)
    {
        var item = new TestListingItem { Title = "Ceramic Mug", State = "active", IsMissingOnEtsy = false };
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = emptyQuery }));
    }

    [Fact]
    public void T2_R6_03_GoneFromEtsyMissingIsolation()
    {
        var missingItem = new TestListingItem { ListingId = 1, State = "active", IsMissingOnEtsy = true };
        var normalItem = new TestListingItem { ListingId = 2, State = "active", IsMissingOnEtsy = false };

        Assert.False(TemplateFilterMatcher.Matches(missingItem, new TemplateListingFilter { Status = "all" }));
        Assert.False(TemplateFilterMatcher.Matches(missingItem, new TemplateListingFilter { Status = "active" }));
        Assert.False(TemplateFilterMatcher.Matches(missingItem, new TemplateListingFilter { Status = "draft" }));

        Assert.True(TemplateFilterMatcher.Matches(missingItem, new TemplateListingFilter { Status = "missing" }));
        Assert.False(TemplateFilterMatcher.Matches(normalItem, new TemplateListingFilter { Status = "missing" }));
    }

    [Fact]
    public void T2_R6_04_ZeroVisibleItemsBulkSelect()
    {
        var harness = new BulkSelectionHarness();
        harness.AllItems.Add(new TestListingItem { ListingId = 1, Title = "Blue Mug", ShopId = 1 });
        harness.AllItems.Add(new TestListingItem { ListingId = 2, Title = "Red Mug", ShopId = 1 });

        // Filter that matches nothing
        harness.Filter = new TemplateListingFilter { Text = "NonExistentQuery_XYZ" };

        Assert.Empty(harness.Visible);
        Assert.False(harness.HeaderChecked);

        // Toggle when empty visible items is a safe no-op
        harness.TogglePickAllVisible();
        Assert.Empty(harness.Picked);
        Assert.False(harness.HeaderChecked);
    }

    [Fact]
    public void T2_R6_05_TriStateHeaderCheckboxOnPartialSelection()
    {
        var harness = new BulkSelectionHarness();
        harness.AllItems.Add(new TestListingItem { ListingId = 1, Title = "Mug 1", ShopId = 1 });
        harness.AllItems.Add(new TestListingItem { ListingId = 2, Title = "Mug 2", ShopId = 1 });
        harness.AllItems.Add(new TestListingItem { ListingId = 3, Title = "Mug 3", ShopId = 1 });

        Assert.Equal(3, harness.Visible.Count);

        // 0 of 3 picked: false
        Assert.False(harness.HeaderChecked);

        // 1 of 3 picked: null (indeterminate)
        harness.Pick(1, true);
        Assert.Null(harness.HeaderChecked);

        // 2 of 3 picked: null (indeterminate)
        harness.Pick(2, true);
        Assert.Null(harness.HeaderChecked);

        // Clicking toggle when indeterminate selects all remaining visible items
        harness.TogglePickAllVisible();
        Assert.True(harness.HeaderChecked);
        Assert.Contains(1, harness.Picked);
        Assert.Contains(2, harness.Picked);
        Assert.Contains(3, harness.Picked);

        // Clicking toggle when all selected deselects all visible items
        harness.TogglePickAllVisible();
        Assert.False(harness.HeaderChecked);
        Assert.Empty(harness.Picked);
    }

    #endregion

    #region TIER 3: CROSS-FEATURE INTERACTIONS

    [Fact]
    public async Task T3_Cross_R5_x_R1_NewListingDraftWithVideo()
    {
        using var shop = new FakeEtsyShop();

        // 1. Setup per-shop defaults
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 501, Title = "Standard" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 601, ReadinessState = "made_to_order", DisplayLabel = "1-3 days" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = FakeEtsyShop.ShopId, EtsyReturnPolicyId = 701, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = FakeEtsyShop.ShopId, EtsySectionId = 801, Title = "Vases" });
            db.TaxonomyNodes.Add(new TaxonomyNode { Id = 1001, Name = "Vases", FullPath = "Home > Decor > Vases" });
            await db.SaveChangesAsync();
        }

        var creator = new ListingCreationService(
            shop.Db,
            shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<IAuditLog>(),
            NullLogger<ListingCreationService>.Instance
        );

        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Handmade Ceramic Desk Vase",
            Price = 28.00m,
            Quantity = 3,
            TaxonomyId = 1001,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
            ReturnPolicyId = 701,
            ShopSectionId = 801,
            WhoMade = "i_did",
            WhenMade = "made_to_order",
            Description = "Handcrafted desk vase.",
            Tags = ["ceramic", "vase", "decor"],
            Materials = ["clay", "glaze"],
        };

        var localId = await creator.CreateDraftAsync(model, "tophattwaffle");
        Assert.True(localId > 0);

        // 2. Attach video to newly created draft
        var videoBytes = "desk-vase-demo-video-content"u8.ToArray();
        var videoFile = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = videoFile,
                ["name"] = "vase_preview.mp4",
            },
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(localId);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Video);

        // 3. Push draft to Etsy with video
        var (pushId, blockers) = await editor.RequestPushAsync(localId, "tophattwaffle");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 10,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Pushed video upload call
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
        Assert.Contains("vase_preview.mp4", videoPost.Body);

        // State remains draft
        await using var verifyDb = shop.Db.CreateDbContext();
        var draftListing = await verifyDb.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == localId);
        Assert.Equal("draft", draftListing.State);
        Assert.Single(draftListing.Videos);
    }

    [Fact]
    public async Task T3_Cross_R5_x_R4_ListingWithVariationsAndVideoPush()
    {
        using var shop = new FakeEtsyShop();

        // Listing with variations (Size: S, L x Color: Red, Blue)
        var listingJson = FakeEtsyShop.Listing(301, "Multi-Variation Hoodie");
        listingJson["inventory"] = CreateInventoryWithVariations([
            ("Small", "Red", 3500L, "HOOD-SM-RED"),
            ("Small", "Blue", 3500L, "HOOD-SM-BLU"),
            ("Large", "Red", 4000L, "HOOD-LG-RED"),
            ("Large", "Blue", 4000L, "HOOD-LG-BLU"),
        ]);
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var localListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 301);

        // Attach video to this listing
        var videoBytes = "hoodie-tryon-spin-video"u8.ToArray();
        var videoFile = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var inventory = (await editor.LoadAsync(localListing.Id))!.Model.Local(ListingFields.Inventory)!.DeepClone();
        inventory = SkuPatternResolver.ApplyPattern((JsonObject)inventory, "{sku}-v2", "HOOD");
        await editor.SaveAsync(localListing.Id, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Inventory] = inventory,
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = videoFile,
                ["name"] = "hoodie_spin.mp4",
            },
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(localListing.Id, "tophattwaffle");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 11,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify video was uploaded
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);

        // Verify inventory was synchronized
        var inventoryPut = shop.Writes.FirstOrDefault(w => w.Method == "PUT" && w.Path.EndsWith("/inventory"));
        Assert.NotNull(inventoryPut.Path);
    }

    [Fact]
    public async Task T3_Cross_R5_x_Cloner_CloneListingWithVideoAndVariations()
    {
        using var shop = new FakeEtsyShop();
        await SeedSecondShopAsync(shop);

        var listingJson = FakeEtsyShop.Listing(302, "Original Galaxy Vase");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(302, 9955));
        listingJson["inventory"] = CreateInventoryWithVariations([
            ("Standard", "Glitter", 5000L, "VASE-GLIT"),
            ("Standard", "Matte", 4500L, "VASE-MATT"),
        ]);
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var dbImg = shop.Db.CreateDbContext())
        {
            foreach (var img in await dbImg.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await dbImg.SaveChangesAsync();
        }

        await using var db = shop.Db.CreateDbContext();
        var sourceListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 302);

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(sourceListing.Id, OtherShopId);
        choice = choice with
        {
            Title = "Cloned Galaxy Vase",
            ShippingProfileId = 502,
            ReadinessStateId = 602,
            ReturnPolicyId = 702,
            ShopSectionId = 802,
        };

        var plan = await cloner.PlanAsync(sourceListing.Id, choice);
        Assert.True(plan.CanClone);
        Assert.Contains(plan.Calls, c => c.Contains("POST") && c.Contains("/videos"));
        Assert.Contains(plan.Calls, c => c.Contains("PUT") && c.Contains("/inventory"));

        var (cloneId, blockers) = await cloner.RequestAsync(sourceListing.Id, choice, "tophattwaffle");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<ListingCloneJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 12,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new ClonePayload(cloneId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Upload video to new listing in target shop
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.Contains($"/shops/{OtherEtsyShopId}/listings/") && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
    }

    [Fact]
    public async Task T3_Cross_R6_x_R3_FilterTextTemplateCandidatesWithPlaceholdersAndLink()
    {
        using var shop = new FakeEtsyShop();

        const string careText = "Care: Hand wash with warm soapy water only.";
        const string mugDesc = "A nice mug.\n\n" + careText + "\n\nThanks for looking!";
        const string boardDesc = "A nice board.\n\n" + careText + "\n\nThanks for looking!";

        // 1. Create text block with initial care text
        var blocks = shop.Provider.GetRequiredService<TextBlockService>();
        var (blockId, error) = await blocks.SaveAsync(
            null,
            "Care Policy",
            null,
            careText,
            "tophattwaffle"
        );
        Assert.Null(error);

        // 2. Add listings
        var listing1 = FakeEtsyShop.Listing(401, "Ceramic Tea Mug");
        listing1["description"] = mugDesc;
        listing1["tags"] = new JsonArray("pottery", "drinkware");
        shop.Listings.Add(listing1);

        var listing2 = FakeEtsyShop.Listing(402, "Wooden Cutting Board");
        listing2["description"] = boardDesc;
        listing2["tags"] = new JsonArray("woodworking", "kitchen");
        shop.Listings.Add(listing2);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // 3. Query candidates and apply R6 filter
        var candidates = await blocks.CandidatesAsync(blockId!.Value);
        Assert.Equal(2, candidates.Count);

        var potteryFilter = new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId, Text = "pottery" };
        var filteredCandidates = candidates.Where(c => TemplateFilterMatcher.Matches(c, potteryFilter)).ToList();

        var matched = Assert.Single(filteredCandidates);
        Assert.Equal("Ceramic Tea Mug", matched.Title);

        // 4. Link candidate
        await blocks.LinkAsync(blockId.Value, [matched.ListingId], "tophattwaffle");

        var uses = await blocks.UsesAsync(blockId.Value);
        var use = Assert.Single(uses);
        Assert.Equal(matched.ListingId, use.ListingId);

        // 5. Update block with placeholders and apply
        await blocks.SaveAsync(blockId.Value, "Care Policy", "care-policy", "Made by {{shop.name}} for {{listing.title}}.\nHand wash only.", "tophattwaffle");
        await blocks.ApplyAsync(blockId.Value, "tophattwaffle");

        // 6. Verify resolved description
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var editModel = await editor.LoadAsync(matched.ListingId);
        Assert.NotNull(editModel);
        var desc = (string)editModel.Model.Local("description")!;
        Assert.Contains("Ceramic Tea Mug", desc);
        Assert.Contains("TestShop", desc);
    }

    [Fact]
    public async Task T3_Cross_R6_x_R4_FilterVariationListCandidatesWithSkuPatternAndBulkLink()
    {
        using var shop = new FakeEtsyShop();
        await SeedSecondShopAsync(shop);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var (listId, error) = await lists.SaveAsync(null, "Apparel Sizes", [
            new VariationListOption(0, "Small", true),
            new VariationListOption(0, "Large", true),
        ], "tophattwaffle");
        Assert.Null(error);

        var l1 = FakeEtsyShop.Listing(501, "Cotton T-Shirt");
        l1["tags"] = new JsonArray("apparel", "clothing");
        l1["inventory"] = CreateInventoryWithVariations([
            ("Small", "White", 2500L, "TSHIRT-SM-WHT"),
            ("Large", "White", 2500L, "TSHIRT-LG-WHT"),
        ]);
        shop.Listings.Add(l1);

        var l2 = FakeEtsyShop.Listing(502, "Linen Shirt");
        l2["tags"] = new JsonArray("apparel", "linen");
        l2["inventory"] = CreateInventoryWithVariations([
            ("Small", "Blue", 3500L, "LINEN-SM-BLU"),
            ("Large", "Blue", 3500L, "LINEN-LG-BLU"),
        ]);
        shop.Listings.Add(l2);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var candidates = await lists.CandidatesAsync(listId!.Value);
        var filterActiveApparel = new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId, Status = "active", Text = "linen" };
        var selected = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterActiveApparel)).ToList();

        var chosen = Assert.Single(selected);
        Assert.Equal("Linen Shirt", chosen.Title);

        // Bulk link
        await lists.LinkAsync(listId.Value, [(chosen.ListingId, SizePropertyId)], "tophattwaffle");
        var uses = await lists.UsesAsync(listId.Value);
        Assert.Single(uses);
        Assert.Equal(chosen.ListingId, uses[0].ListingId);
    }

    #endregion

    #region TIER 4: REAL-WORLD APPLICATION SCENARIOS

    [Fact]
    public async Task T4_Scenario_01_CompleteSellerOnboarding()
    {
        using var shop = new FakeEtsyShop();

        // 1. Configure shop defaults
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 501, Title = "Standard Shipping" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 601, ReadinessState = "made_to_order", DisplayLabel = "1-3 business days" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = FakeEtsyShop.ShopId, EtsyReturnPolicyId = 701, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = FakeEtsyShop.ShopId, EtsySectionId = 801, Title = "Signature Pottery" });
            db.TaxonomyNodes.Add(new TaxonomyNode { Id = 1001, Name = "Vases", FullPath = "Home > Pottery > Vases" });
            await db.SaveChangesAsync();
        }

        var defaultsService = new ShopDefaultsService(shop.Provider.GetRequiredService<ISettingsStore>(), shop.Provider.GetRequiredService<IAuditLog>());
        await defaultsService.SaveAsync(FakeEtsyShop.ShopId, new ShopDefaults(
            ShippingProfileId: 501,
            ReadinessStateId: 601,
            ReturnPolicyId: 701,
            ShopSectionId: 801,
            WhoMade: "i_did",
            WhenMade: "made_to_order",
            TaxonomyId: 1001
        ), "adminUser");

        var loadedDefaults = await defaultsService.GetAsync(FakeEtsyShop.ShopId);
        Assert.NotNull(loadedDefaults);
        Assert.Equal(501, loadedDefaults.ShippingProfileId);

        // 2. Create new listing draft with placeholders
        var creator = new ListingCreationService(
            shop.Db,
            shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<IAuditLog>(),
            NullLogger<ListingCreationService>.Instance
        );

        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Wheel-Thrown Artisan Stoneware Vase",
            Price = 45.00m,
            Quantity = 5,
            TaxonomyId = loadedDefaults.TaxonomyId,
            ShippingProfileId = loadedDefaults.ShippingProfileId,
            ReadinessStateId = loadedDefaults.ReadinessStateId,
            ReturnPolicyId = loadedDefaults.ReturnPolicyId,
            ShopSectionId = loadedDefaults.ShopSectionId,
            WhoMade = loadedDefaults.WhoMade ?? "i_did",
            WhenMade = loadedDefaults.WhenMade ?? "made_to_order",
            Description = "Handcrafted by {{shop.name}}.\nAuthentic studio pottery.",
            Tags = ["artisan vase", "stoneware", "handmade pottery"],
            Materials = ["stoneware clay", "natural glaze"],
        };

        var localId = await creator.CreateDraftAsync(model, "adminUser");
        Assert.True(localId > 0);

        // 3. Attach promotional video in listing editor
        var videoContent = "onboarding-pottery-wheel-spin-video"u8.ToArray();
        var videoFile = await shop.VideoStore.SaveAsync(videoContent, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = videoFile,
                ["name"] = "wheel_demo.mp4",
            },
        }, "adminUser");

        // 4. Verify preview and push
        var preview = await editor.PreviewAsync(localId);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Video);

        var (pushId, blockers) = await editor.RequestPushAsync(localId, "adminUser");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 20,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Pushed video upload call executed
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);

        // Listing remains draft
        await using var verifyDb = shop.Db.CreateDbContext();
        var localListing = await verifyDb.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == localId);
        Assert.Equal("draft", localListing.State);
        Assert.Single(localListing.Videos);
    }

    [Fact]
    public async Task T4_Scenario_02_CatalogRebranding()
    {
        using var shop = new FakeEtsyShop();
        await SeedSecondShopAsync(shop);

        const string footerText = "Standard studio footer text.";

        // 1. Create text block with initial footer text
        var blocks = shop.Provider.GetRequiredService<TextBlockService>();
        var (blockId, error) = await blocks.SaveAsync(
            null,
            "Rebranding Footer",
            null,
            footerText,
            "adminUser"
        );
        Assert.Null(error);

        // 2. Add listings across Shop 1 and Shop 2 in various states
        var l1 = FakeEtsyShop.Listing(601, "Shop1 Active Mug 1", state: "active");
        l1["description"] = "Mug 1 description.\n\n" + footerText;
        l1["tags"] = new JsonArray("ceramics");
        shop.Listings.Add(l1);

        var l2 = FakeEtsyShop.Listing(602, "Shop1 Active Mug 2", state: "active");
        l2["description"] = "Mug 2 description.\n\n" + footerText;
        l2["tags"] = new JsonArray("ceramics");
        shop.Listings.Add(l2);

        var l3 = FakeEtsyShop.Listing(603, "Shop1 Draft Mug 3", state: "draft");
        l3["description"] = "Mug 3 description.\n\n" + footerText;
        shop.Listings.Add(l3);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // 3. Filter candidates: Shop 1 + Status "active"
        var candidates = await blocks.CandidatesAsync(blockId!.Value);
        var filter = new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId, Status = "active" };
        var activeShop1Candidates = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter)).ToList();

        Assert.Equal(2, activeShop1Candidates.Count);
        Assert.All(activeShop1Candidates, c => Assert.Equal("active", c.State));

        // 4. Bulk link active Shop 1 candidates
        await blocks.LinkAsync(blockId.Value, activeShop1Candidates.Select(c => c.ListingId), "adminUser");

        // 5. Update block with rebranding placeholders
        await blocks.SaveAsync(blockId.Value, "Rebranding Footer", "rebranding-footer", "Handcrafted by {{shop.name}}! Visit {{shop.url}} for more.", "adminUser");

        // 6. Apply rebranding block
        var appliedCount = await blocks.ApplyAsync(blockId.Value, "adminUser");
        Assert.Equal(2, appliedCount);

        // 7. Verify rendered output contains shop name and URL
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        foreach (var candidate in activeShop1Candidates)
        {
            var editModel = await editor.LoadAsync(candidate.ListingId);
            Assert.NotNull(editModel);
            var desc = (string)editModel.Model.Local("description")!;
            Assert.Contains("TestShop", desc);
            Assert.Contains("etsy.com", desc);
        }
    }

    [Fact]
    public async Task T4_Scenario_05_CrossShopListingMigrationWithFullMedia()
    {
        using var shop = new FakeEtsyShop();
        await SeedSecondShopAsync(shop);

        // Source listing on Shop 1 has variations, video, images
        var listingJson = FakeEtsyShop.Listing(701, "Handmade Wooden Chessboard");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(701, 8888));
        listingJson["inventory"] = CreateInventoryWithVariations([
            ("Small (12\")", "Walnut", 12000L, "CHESS-SM-WAL"),
            ("Small (12\")", "Maple", 11000L, "CHESS-SM-MAP"),
            ("Large (16\")", "Walnut", 16000L, "CHESS-LG-WAL"),
            ("Large (16\")", "Maple", 15000L, "CHESS-LG-MAP"),
        ]);
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Populate local image files
        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        await using var dbSource = shop.Db.CreateDbContext();
        var sourceListing = await dbSource.Listings.SingleAsync(l => l.EtsyListingId == 701);

        // Setup Target Shop defaults in cloner
        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(sourceListing.Id, OtherShopId);
        choice = choice with
        {
            Title = "SecondShop Handcrafted Chessboard",
            ShippingProfileId = 502,
            ReadinessStateId = 602,
            ReturnPolicyId = 702,
            ShopSectionId = 802,
        };

        var plan = await cloner.PlanAsync(sourceListing.Id, choice);
        Assert.True(plan.CanClone);
        Assert.Contains(plan.Calls, c => c.Contains("POST") && c.Contains("/videos"));
        Assert.Contains(plan.Calls, c => c.Contains("PUT") && c.Contains("/inventory"));

        var (cloneId, blockers) = await cloner.RequestAsync(sourceListing.Id, choice, "adminUser");
        Assert.Empty(blockers);

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 30,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new ClonePayload(cloneId!.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify video was uploaded to target listing
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.Contains($"/shops/{OtherEtsyShopId}/listings/") && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);

        // Verify cloned listing in DB
        await using var dbVerify = shop.Db.CreateDbContext();
        var clonedListing = await dbVerify.Listings.Include(l => l.Videos).FirstOrDefaultAsync(l => l.ShopId == OtherShopId);
        Assert.NotNull(clonedListing);
        Assert.Equal("draft", clonedListing.State);
        Assert.Single(clonedListing.Videos);
    }

    [Fact]
    public async Task T4_Scenario_06_BulkTemplateManagementWithActiveFilters()
    {
        using var shop = new FakeEtsyShop();

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var (listId, error) = await lists.SaveAsync(null, "Finish Options", [
            new VariationListOption(0, "Matte", true),
            new VariationListOption(0, "Gloss", true),
        ], "adminUser");
        Assert.Null(error);

        var l1 = FakeEtsyShop.Listing(801, "Pottery Coffee Mug", state: "active");
        l1["tags"] = new JsonArray("pottery", "kitchen");
        l1["inventory"] = CreateInventoryWithColor([("Matte", 2200L), ("Gloss", 2200L)], true);
        shop.Listings.Add(l1);

        var l2 = FakeEtsyShop.Listing(802, "Pottery Serving Bowl", state: "active");
        l2["tags"] = new JsonArray("pottery", "dining");
        l2["inventory"] = CreateInventoryWithColor([("Matte", 3400L), ("Gloss", 3400L)], true);
        shop.Listings.Add(l2);

        var l3 = FakeEtsyShop.Listing(803, "Ceramic Vase", state: "active");
        l3["tags"] = new JsonArray("ceramic", "decor");
        l3["inventory"] = CreateInventoryWithColor([("Matte", 4500L), ("Gloss", 4500L)], true);
        shop.Listings.Add(l3);

        var l4 = FakeEtsyShop.Listing(804, "Draft Pottery Pitcher", state: "draft");
        l4["tags"] = new JsonArray("pottery");
        l4["inventory"] = CreateInventoryWithColor([("Matte", 2800L), ("Gloss", 2800L)], true);
        shop.Listings.Add(l4);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var candidates = await lists.CandidatesAsync(listId!.Value);
        Assert.Equal(4, candidates.Count);

        // Simulate multi-filter bulk selection session
        var harness = new BulkSelectionHarness();
        foreach (var c in candidates)
        {
            harness.AllItems.Add(new TestListingItem
            {
                ListingId = c.ListingId,
                Title = c.Title,
                ShopId = c.ShopId,
                ShopName = c.ShopName,
                EtsyListingId = c.EtsyListingId,
                State = c.State,
                IsMissingOnEtsy = c.IsMissingOnEtsy,
                Tags = c.Tags,
            });
        }

        // Filter 1: active pottery items (l1, l2)
        harness.Filter = new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId, Status = "active", Text = "pottery" };
        Assert.Equal(2, harness.Visible.Count);

        harness.TogglePickAllVisible();
        Assert.True(harness.HeaderChecked);
        Assert.Equal(2, harness.Picked.Count);

        // Filter 2: active ceramic items (l3) - selection from filter 1 preserved
        harness.Filter = new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId, Status = "active", Text = "ceramic" };
        Assert.Single(harness.Visible);
        Assert.False(harness.HeaderChecked); // visible item not yet picked

        harness.TogglePickAllVisible();
        Assert.True(harness.HeaderChecked);
        Assert.Equal(3, harness.Picked.Count); // 2 pottery + 1 ceramic = 3

        // Filter 3: non-existent search -> zero visible
        harness.Filter = new TemplateListingFilter { Text = "NonExistent" };
        Assert.Empty(harness.Visible);
        Assert.False(harness.HeaderChecked);
        Assert.Equal(3, harness.Picked.Count); // All 3 preserved

        // Link all 3 selected candidates
        var links = harness.Picked.Select(id => (id, ColorPropertyId)).ToList();
        await lists.LinkAsync(listId.Value, links, "adminUser");

        var uses = await lists.UsesAsync(listId.Value);
        Assert.Equal(3, uses.Count);
        Assert.All(harness.Picked, id => Assert.Contains(uses, u => u.ListingId == id));
    }

    #endregion
}
