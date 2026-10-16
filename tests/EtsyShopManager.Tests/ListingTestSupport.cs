using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>SQLite in-memory database with the real migrations applied. One shared open connection per test.</summary>
internal sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public AppDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>A temporary data root for the image store, deleted afterwards.</summary>
internal sealed class TempStorage : IDisposable
{
    public StorageOptions Options { get; } = new()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "esm-tests", Guid.NewGuid().ToString("N")),
    };

    public TempStorage() => Options.EnsureDirectories();

    public void Dispose()
    {
        try { Directory.Delete(Options.ResolvedRoot, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// A fake Etsy shop (Etsy shop 42, local shop 1) served through the real client stack: listings per state with
/// paging, lookups, attributes, taxonomy, and CDN image downloads. Mutate <see cref="Listings"/> between imports.
/// </summary>
internal sealed class FakeEtsyShop : IDisposable
{
    public const long EtsyShopId = 42;
    public const int ShopId = 1;

    public TestDb Db { get; } = new();
    public TempStorage Storage { get; } = new();
    public ImmediateTimeProvider Time { get; } = new();
    public ScriptedHandler Api { get; }
    public ScriptedHandler Cdn { get; }
    public ServiceProvider Provider { get; }

    /// <summary>The shop's listings as JSON objects (Etsy's shape). Missing "images"/"inventory" forces the fallback calls.</summary>
    public List<JsonObject> Listings { get; } = [];

    /// <summary>Paths that fail with 500 (e.g. to interrupt an import). Remove an entry to let it succeed again.</summary>
    public HashSet<string> FailingStates { get; } = [];

    /// <summary>The shop's receipts (Etsy's shape). Paged by offset, filtered by min_last_modified, sorted as asked.</summary>
    public List<JsonObject> Receipts { get; } = [];

    /// <summary>Runs after each receipts page is served (to change receipts in the middle of a poll).</summary>
    public Action<int>? AfterReceiptsPage { get; set; }

    /// <summary>Receipt offsets that fail with 500 (to interrupt a poll).</summary>
    public HashSet<int> FailingReceiptOffsets { get; } = [];

    /// <summary>Listings only the batch endpoint knows (e.g. deleted from the shop but still on old orders).</summary>
    public List<JsonObject> BatchOnlyListings { get; } = [];

    public RecordingPublisher Events { get; } = new();

    public ListingImporter Importer => Provider.GetRequiredService<ListingImporter>();
    public ImageDownloader Downloader => Provider.GetRequiredService<ImageDownloader>();
    public TaxonomyImporter Taxonomy => Provider.GetRequiredService<TaxonomyImporter>();
    public ImageStore Store => Provider.GetRequiredService<ImageStore>();

    public FakeEtsyShop()
    {
        Api = new ScriptedHandler((req, _) => RespondApi(req));
        Cdn = new ScriptedHandler((req, _) => RespondCdn(req));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IEtsyCredentialsProvider, FakeCredentials>();
        var tokens = new InMemoryTokenStore();
        tokens.Tokens[ShopId] = new EtsyTokenSet
        {
            AccessToken = "111.access",
            RefreshToken = "111.refresh",
            AccessTokenExpiresAtUtc = Time.Now.UtcDateTime.AddYears(10),
            RefreshTokenIssuedAtUtc = Time.Now.UtcDateTime,
        };
        services.AddSingleton<IEtsyTokenStore>(tokens);
        services.AddSingleton<IDomainEventPublisher>(Events);
        services.AddSingleton<IAuditLog, NullAuditLog>();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(Db);
        services.AddSingleton(Storage.Options);
        services.AddEtsy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Etsy:RequestsPerSecond"] = "1000",
            ["Etsy:BurstSize"] = "1000",
        }).Build());
        services.AddHttpClient(EtsyOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Api);
        services.AddHttpClient(EtsyServiceCollectionExtensions.EtsyImageClientName).ConfigurePrimaryHttpMessageHandler(() => Cdn);
        services.AddSingleton<ImageStore>();
        services.AddSingleton<ListingImporter>();
        services.AddSingleton<TaxonomyImporter>();
        services.AddSingleton<ImageDownloader>();
        services.AddSingleton<ListingQueries>();
        services.AddSingleton<OrderPoller>();
        services.AddSingleton<OrderQueries>();
        services.AddSingleton<ApiKeyService>();
        Provider = services.BuildServiceProvider();

        using var db = Db.CreateDbContext();
        db.Shops.Add(new Shop
        {
            Id = ShopId,
            EtsyShopId = EtsyShopId,
            EtsyUserId = 111,
            Name = "TestShop",
            ConnectedAtUtc = Time.Now.UtcDateTime,
            Token = new ShopToken { EncryptedAccessToken = "x", EncryptedRefreshToken = "x", Scopes = "listings_r" },
        });
        db.SaveChanges();
    }

    public List<string> RequestedPaths => Api.Requests.Select(r => r.RequestUri!.PathAndQuery).ToList();

    public static JsonObject Listing(long id, string title, string state = "active", int quantity = 5,
        bool withImages = true, bool withInventory = true, long updated = 1_760_000_000)
    {
        var listing = new JsonObject
        {
            ["listing_id"] = id,
            ["shop_id"] = EtsyShopId,
            ["user_id"] = 111,
            ["title"] = title,
            ["description"] = $"Description of {title}\r\nSecond line",
            ["state"] = state,
            ["quantity"] = quantity,
            ["created_timestamp"] = 1_700_000_000 + id,
            ["last_modified_timestamp"] = updated,
            ["updated_timestamp"] = updated,
            ["tags"] = new JsonArray("vase", "pla"),
            ["materials"] = new JsonArray("PLA"),
            ["style"] = new JsonArray(),
            ["shipping_profile_id"] = 501,
            ["return_policy_id"] = 701,
            ["readiness_state_id"] = 601,
            ["shop_section_id"] = 801,
            ["taxonomy_id"] = 1001,
            ["who_made"] = "i_did",
            ["when_made"] = "made_to_order",
            ["is_supply"] = false,
            ["listing_type"] = "physical",
            ["has_variations"] = true,
            ["should_auto_renew"] = true,
            ["is_taxable"] = true,
            ["featured_rank"] = -1,
            ["url"] = $"https://www.etsy.com/listing/{id}",
            ["price"] = Money(1999),
            ["skus"] = new JsonArray($"SKU{id}-S", $"SKU{id}-L"),
            ["production_partners"] = new JsonArray(),
            ["personalization"] = new JsonObject { ["personalization_questions"] = new JsonArray() },
        };
        if (withImages) listing["images"] = Images(id);
        if (withInventory) listing["inventory"] = Inventory(id, quantity);
        return listing;
    }

    public static JsonObject Money(long amount) =>
        new() { ["amount"] = amount, ["divisor"] = 100, ["currency_code"] = "USD" };

    public static JsonArray Images(long listingId) => new(
        Image(listingId, listingId * 10 + 1, 1),
        Image(listingId, listingId * 10 + 2, 2));

    public static JsonObject Image(long listingId, long imageId, int rank) => new()
    {
        ["listing_id"] = listingId,
        ["listing_image_id"] = imageId,
        ["rank"] = rank,
        ["url_570xN"] = $"https://i.etsystatic.com/{imageId}/il_570xN.jpg",
        ["url_fullxfull"] = $"https://i.etsystatic.com/{imageId}/il_fullxfull.jpg",
        ["full_width"] = 2000,
        ["full_height"] = 1500,
        ["alt_text"] = $"Photo {rank}",
    };

    public static JsonObject Inventory(long listingId, int quantity) => new()
    {
        ["price_on_property"] = new JsonArray(100),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(100),
        ["products"] = new JsonArray(
            Product(listingId * 100 + 1, $"SKU{listingId}-S", "Small", 1999, quantity),
            Product(listingId * 100 + 2, $"SKU{listingId}-L", "Large", 2999, quantity)),
    };

    public static JsonObject Product(long productId, string sku, string size, long price, int quantity) => new()
    {
        ["product_id"] = productId,
        ["sku"] = sku,
        ["is_deleted"] = false,
        ["property_values"] = new JsonArray(new JsonObject
        {
            ["property_id"] = 100,
            ["property_name"] = "Size",
            ["value_ids"] = new JsonArray(),
            ["values"] = new JsonArray(size),
        }),
        ["offerings"] = new JsonArray(new JsonObject
        {
            ["offering_id"] = productId * 10,
            ["quantity"] = quantity,
            ["is_enabled"] = true,
            ["is_deleted"] = false,
            ["price"] = Money(price),
        }),
    };

    private HttpResponseMessage RespondApi(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        var shop = $"/v3/application/shops/{EtsyShopId}";

        if (path == $"{shop}/listings")
        {
            var state = query["state"]!;
            if (FailingStates.Contains(state)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var offset = int.Parse(query["offset"] ?? "0");
            var limit = int.Parse(query["limit"] ?? "25");
            var inState = Listings.Where(l => (string)l["state"]! == state).ToList();
            return Page(inState.Skip(offset).Take(limit), inState.Count);
        }

        if (path == $"{shop}/receipts")
        {
            var offset = int.Parse(query["offset"] ?? "0");
            if (FailingReceiptOffsets.Contains(offset)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var limit = int.Parse(query["limit"] ?? "25");
            var since = long.TryParse(query["min_last_modified"], out var m) ? m : 0;
            var sortKey = query["sort_on"] == "updated" ? "updated_timestamp" : "created_timestamp";
            var matching = Receipts.Where(r => (long)r["updated_timestamp"]! >= since)
                .OrderBy(r => (long)r[sortKey]!).ThenBy(r => (long)r["receipt_id"]!).ToList();
            var response = Page(matching.Skip(offset).Take(limit), matching.Count);
            AfterReceiptsPage?.Invoke(offset);
            return response;
        }
        if (path == "/v3/application/listings/batch")
        {
            var ids = (query["listing_ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
            return Page(Listings.Concat(BatchOnlyListings).Where(l => ids.Contains((long)l["listing_id"]!)));
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith($"{shop}/listings/") && path.EndsWith("/properties"))
        {
            if (ListingsWithoutProperties.Contains(long.Parse(parts[5]))) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Page([new JsonObject
            {
                ["property_id"] = 200,
                ["property_name"] = "Primary color",
                ["value_ids"] = new JsonArray(1),
                ["values"] = new JsonArray("Black"),
            }]);
        }
        if (path.StartsWith("/v3/application/listings/") && path.EndsWith("/images"))
            return Page(Images(long.Parse(parts[3])).Select(n => (JsonObject)n!.DeepClone()));
        if (path.StartsWith("/v3/application/listings/") && path.EndsWith("/inventory"))
            return Json(Inventory(long.Parse(parts[3]), 5));

        return path switch
        {
            _ when path == $"{shop}/shipping-profiles" => Page([new JsonObject
            {
                ["shipping_profile_id"] = 501, ["title"] = "Standard", ["origin_country_iso"] = "US", ["profile_type"] = "manual",
                ["is_deleted"] = false, ["shipping_profile_destinations"] = new JsonArray(new JsonObject()),
                ["shipping_profile_upgrades"] = new JsonArray(),
            }]),
            _ when path == $"{shop}/readiness-state-definitions" => Page([new JsonObject
            {
                ["readiness_state_id"] = 601, ["readiness_state"] = "made_to_order", ["min_processing_days"] = 1,
                ["max_processing_days"] = 3, ["processing_days_display_label"] = "1-3 days",
            }]),
            _ when path == $"{shop}/policies/return" => Page([new JsonObject
            {
                ["return_policy_id"] = 701, ["accepts_returns"] = true, ["accepts_exchanges"] = false, ["return_deadline"] = 30,
            }]),
            _ when path == $"{shop}/sections" => Page([new JsonObject
            {
                ["shop_section_id"] = 801, ["title"] = "Vases", ["rank"] = 1, ["active_listing_count"] = 3,
            }]),
            _ when path == $"{shop}/production-partners" => Page([]),
            "/v3/application/seller-taxonomy/nodes" => Page([new JsonObject
            {
                ["id"] = 1, ["level"] = 0, ["name"] = "Home & Living",
                ["children"] = new JsonArray(new JsonObject
                {
                    ["id"] = 1001, ["level"] = 1, ["name"] = "Vases", ["parent_id"] = 1, ["children"] = new JsonArray(),
                }),
            }]),
            "/v3/application/seller-taxonomy/nodes/1001/properties" => Page([new JsonObject
            {
                ["property_id"] = 200, ["name"] = "Primary color", ["display_name"] = "Primary color",
                ["supports_attributes"] = true, ["supports_variations"] = true,
                ["possible_values"] = new JsonArray(new JsonObject { ["value_id"] = 1, ["name"] = "Black" }),
            }]),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage RespondCdn(HttpRequestMessage req)
    {
        // Distinct bytes per URL, so each image gets its own content hash.
        var bytes = System.Text.Encoding.UTF8.GetBytes("JPEG:" + req.RequestUri!.AbsolutePath);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private static HttpResponseMessage Page(IEnumerable<JsonObject> items, int? count = null)
    {
        var array = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray());
        return Json(new JsonObject { ["count"] = count ?? array.Count, ["results"] = array });
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        ScriptedHandler.Json(HttpStatusCode.OK, node.ToJsonString(new JsonSerializerOptions()));

    /// <summary>Listings whose getListingProperties call returns 404.</summary>
    public HashSet<long> ListingsWithoutProperties { get; } = [];

    public void Dispose()
    {
        Provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Db.Dispose();
        Storage.Dispose();
    }
}
