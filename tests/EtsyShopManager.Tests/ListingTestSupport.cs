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
        services.AddSingleton<IEtsyWriteJournal, EtsyShopManager.Web.Infrastructure.EtsyWriteJournal>(); // every write lands in EtsyWriteLogs
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
        services.AddSingleton<RecordingJobQueue>();
        services.AddSingleton<EtsyShopManager.Core.Jobs.IJobQueue>(sp => sp.GetRequiredService<RecordingJobQueue>());
        services.AddSingleton<EtsyShopManager.Web.Modules.Editor.ListingEditor>();
        services.AddSingleton<EtsyShopManager.Web.Modules.Stock.StockRefiller>();
        services.AddSingleton<EtsyShopManager.Web.Modules.Stock.RefillQueue>();
        services.AddTransient<EtsyShopManager.Web.Modules.Editor.PushListingJob>();
        services.AddSingleton<EtsyShopManager.Web.Modules.Editor.PhotoOrderTester>();
        services.AddTransient<EtsyShopManager.Web.Modules.Editor.PhotoOrderTestJob>();
        services.AddSingleton<EtsyShopManager.Core.Services.ISettingsStore, InMemorySettings>();
        services.AddSingleton<EtsyShopManager.Web.Modules.Editor.ListingCloner>();
        services.AddTransient<EtsyShopManager.Web.Modules.Editor.ListingCloneJob>();
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

        if (RespondEdit(req, path, query, shop) is { } edit) return edit;

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
        if (parts.Length == 7 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "properties" && req.Method == HttpMethod.Get)
        {
            if (ListingsWithoutProperties.Contains(long.Parse(parts[5]))) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (Listings.FirstOrDefault(l => (long)l["listing_id"]! == long.Parse(parts[5]))?["_properties"] is JsonArray set) return Page(set.Select(p => (JsonObject)p!.DeepClone()));
            return Page([new JsonObject
            {
                ["property_id"] = 200,
                ["property_name"] = "Primary color",
                ["value_ids"] = new JsonArray(1),
                ["values"] = new JsonArray("Black"),
            }]);
        }
        if (path.StartsWith("/v3/application/listings/") && path.EndsWith("/images"))
            return Page(ShownImages(long.Parse(parts[3])).Select(n => (JsonObject)n!.DeepClone()));
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

    /// <summary>Every write sent to the fake Etsy: method, path and body, in order.</summary>
    public List<(string Method, string Path, string Body)> Writes { get; } = [];

    /// <summary>Fails writes (PATCH/PUT) with this status, e.g. to test a failed push.</summary>
    public HttpStatusCode? FailWritesWith { get; set; }

    /// <summary>Fails only updateListingInventory (PUT) with this status: a push that is half written.</summary>
    public HttpStatusCode? FailInventoryWritesWith { get; set; }

    /// <summary>Rejects photo moves (POST …/images) with this Etsy error message, like the real "inconsistent order".</summary>
    public string? FailImageMovesWith { get; set; }

    public JsonObject ListingJson(long id) => Listings.Single(l => (long)l["listing_id"]! == id);

    private long _nextEtsyId = 900_000;

    // The photos as Etsy returns them: by rank; photos sharing a rank (Etsy allows that) keep their stored order.
    private IEnumerable<JsonNode?> ShownImages(long listingId) =>
        (Listings.FirstOrDefault(l => (long)l["listing_id"]! == listingId)?["images"]?.AsArray() ?? Images(listingId))
        .OrderBy(n => (int)n!["rank"]!);
    private readonly Dictionary<(long PropertyId, string Value), long> _valueIds = [];

    /// <summary>A form field (updateListing / createDraftListing) as Etsy stores it on the listing.</summary>
    private static JsonNode? FormValue(string key, string value) => key switch
    {
        "tags" or "materials" or "styles" => new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode)v).ToArray()),
        "production_partner_ids" => new JsonArray(),
        "is_supply" or "is_taxable" or "should_auto_renew" => value == "true",
        "taxonomy_id" or "shipping_profile_id" or "return_policy_id" or "shop_section_id" => long.Parse(value),
        "item_weight" or "item_length" or "item_width" or "item_height" => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        // Etsy stores the text and returns it HTML-encoded.
        "title" or "description" => value.Replace("'", "&#39;").Replace("\"", "&quot;"),
        _ => value,
    };

    /// <summary>getListing, inventory with the listing, updateListing and updateListingInventory, applied like Etsy would.</summary>
    private HttpResponseMessage? RespondEdit(HttpRequestMessage req, string path, System.Collections.Specialized.NameValueCollection query, string shop)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // GET /v3/application/listings/{id}  (no inventory: Etsy's getListing can't include it)
        if (req.Method == HttpMethod.Get && parts.Length == 4 && parts[2] == "listings" && long.TryParse(parts[3], out var getId))
        {
            var listing = (JsonObject)ListingJson(getId).DeepClone();
            listing.Remove("inventory");
            listing["images"] = new JsonArray([.. ShownImages(getId).Select(n => n!.DeepClone())]);
            return Json(listing);
        }
        // POST /v3/application/shops/42/listings/{id}/images (uploadListingImage), as measured on Etsy (2026-10-17, three
        // photo order test runs): with listing_image_id and a rank the photo gets that rank and no other photo is
        // renumbered, so two photos can share a rank (Etsy then shows them in no fixed order; the fake keeps the stored
        // order). With overwrite=true the photo takes the rank's slot: the photo shown at that rank drops off the listing.
        // A new image file gets the rank given, again without renumbering.
        if (req.Method == HttpMethod.Post && parts.Length == 7 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "images")
        {
            var multipart = (MultipartFormDataContent)req.Content!;
            var file = multipart.FirstOrDefault(p => p.Headers.ContentDisposition?.FileName is not null);
            var fields = multipart.Where(p => p != file).ToDictionary(
                p => p.Headers.ContentDisposition!.Name!.Trim('"'), p => p.ReadAsStringAsync().Result);
            var described = fields.Select(f => $"{f.Key}={f.Value}").ToList();
            if (file is not null)
                described.Insert(0, $"image=<file {file.Headers.ContentDisposition!.FileName!.Trim('"')}, {file.ReadAsByteArrayAsync().Result.Length} bytes>");
            Writes.Add(("POST", path, string.Join("&", described)));
            if (FailImageMovesWith is { } message)
                return ScriptedHandler.Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = message }.ToJsonString());

            var listingId = long.Parse(parts[5]);
            var listing = ListingJson(listingId);
            var images = listing["images"]!.AsArray().Select(i => i!.DeepClone().AsObject()).ToList();
            var rank = int.Parse(fields["rank"]);
            JsonObject photo;
            if (file is not null)
            {
                photo = Image(listingId, ++_nextEtsyId, rank);
                images.Add(photo);
            }
            else
            {
                photo = images.Single(i => (long)i["listing_image_id"]! == long.Parse(fields["listing_image_id"]));
                if (fields.GetValueOrDefault("overwrite") == "true")
                    images.Remove(images.FirstOrDefault(i => i != photo && (int)i["rank"]! == rank)!); // the one shown in that slot
            }
            photo["rank"] = rank;
            if (fields.TryGetValue("alt_text", out var alt)) photo["alt_text"] = alt;
            listing["images"] = new JsonArray([.. images]);
            return ScriptedHandler.Json(HttpStatusCode.Created, photo.ToJsonString());
        }
        // DELETE /v3/application/shops/42/listings/{id}/images/{imageId}
        if (req.Method == HttpMethod.Delete && parts.Length == 8 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "images")
        {
            Writes.Add(("DELETE", path, ""));
            var listingId = long.Parse(parts[5]);
            var listing = ListingJson(listingId);
            var images = listing["images"]!.AsArray().Select(i => i!.DeepClone().AsObject())
                .Where(i => (long)i["listing_image_id"]! != long.Parse(parts[7])).ToList(); // the others keep their ranks (gaps)
            listing["images"] = new JsonArray([.. images]);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        // PATCH /v3/application/shops/42/listings/{id}
        // POST /v3/application/shops/{shopId}/listings (createDraftListing): a new draft with one product.
        if (req.Method == HttpMethod.Post && parts.Length == 5 && parts[2] == "shops" && parts[4] == "listings")
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            Writes.Add(("POST", path, body));
            if (FailWritesWith is { } status) return new HttpResponseMessage(status);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            var id = ++_nextEtsyId;
            var price = (long)Math.Round(decimal.Parse(form["price"]!, System.Globalization.CultureInfo.InvariantCulture) * 100);
            var draft = Listing(id, form["title"]!, state: "draft", quantity: int.Parse(form["quantity"]!), withImages: false, withInventory: false);
            draft["shop_id"] = long.Parse(parts[3]);
            draft["images"] = new JsonArray();
            draft["price"] = Money(price);
            draft["inventory"] = new JsonObject
            {
                ["products"] = new JsonArray(new JsonObject
                {
                    ["product_id"] = ++_nextEtsyId, ["sku"] = "", ["is_deleted"] = false, ["property_values"] = new JsonArray(),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = ++_nextEtsyId, ["quantity"] = int.Parse(form["quantity"]!), ["is_enabled"] = true,
                        ["is_deleted"] = false, ["price"] = Money(price),
                        ["readiness_state_id"] = form["readiness_state_id"] is { } r ? long.Parse(r) : null,
                    }),
                }),
                ["price_on_property"] = new JsonArray(), ["quantity_on_property"] = new JsonArray(),
                ["sku_on_property"] = new JsonArray(), ["readiness_state_on_property"] = new JsonArray(),
            };
            foreach (var key in form.AllKeys.Where(k => k is not ("price" or "quantity" or "readiness_state_id" or "type")))
                draft[key!] = FormValue(key!, form[key]!);
            Listings.Add(draft);
            return ScriptedHandler.Json(HttpStatusCode.Created, draft.ToJsonString());
        }
        // PUT /v3/application/shops/{shopId}/listings/{id}/properties/{propertyId} (updateListingProperty)
        if (req.Method == HttpMethod.Put && parts.Length == 8 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "properties")
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            Writes.Add(("PUT", path, body));
            if (FailWritesWith is { } status) return new HttpResponseMessage(status);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            var listing = ListingJson(long.Parse(parts[5]));
            var set = listing["_properties"] as JsonArray ?? [];
            foreach (var old in set.Where(p => (long)p!["property_id"]! == long.Parse(parts[7])).ToList()) set.Remove(old);
            set.Add(new JsonObject
            {
                ["property_id"] = long.Parse(parts[7]),
                ["scale_id"] = form["scale_id"] is { } scale ? long.Parse(scale) : null,
                ["value_ids"] = new JsonArray([.. (form["value_ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode)long.Parse(v))]),
                ["values"] = new JsonArray([.. (form["values"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode)v)]),
            });
            listing["_properties"] = set;
            return Json(new JsonObject());
        }
        // DELETE /v3/application/shops/{shopId}/listings/{id}/properties/{propertyId} (deleteListingProperty)
        if (req.Method == HttpMethod.Delete && parts.Length == 8 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "properties")
        {
            Writes.Add(("DELETE", path, ""));
            if (FailWritesWith is { } status) return new HttpResponseMessage(status);
            var listing = ListingJson(long.Parse(parts[5]));
            // A listing still on the default properties gets its own list first (as the GET shows it), minus this one.
            var set = listing["_properties"] as JsonArray ?? [new JsonObject
            {
                ["property_id"] = 200, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(1), ["values"] = new JsonArray("Black"),
            }];
            foreach (var old in set.Where(p => (long)p!["property_id"]! == long.Parse(parts[7])).ToList()) set.Remove(old);
            listing["_properties"] = set;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        // POST / DELETE /v3/application/shops/{shopId}/listings/{id}/personalization: Etsy replaces all questions, giving
        // new ones (sent without an id) fresh ids, and returns the add-on price as money.
        if (parts.Length == 7 && parts[2] == "shops" && parts[4] == "listings" && parts[6] == "personalization"
            && (req.Method == HttpMethod.Post || req.Method == HttpMethod.Delete))
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            Writes.Add((req.Method.Method, req.RequestUri!.PathAndQuery, body));
            if (FailWritesWith is { } status) return new HttpResponseMessage(status);
            var listing = ListingJson(long.Parse(parts[5]));
            var questions = new JsonArray();
            if (req.Method == HttpMethod.Post)
                foreach (var q in JsonNode.Parse(body)!["personalization_questions"]!.AsArray())
                {
                    var stored = q!.DeepClone().AsObject();
                    stored["question_id"] = (long?)q["question_id"] ?? ++_nextEtsyId;
                    if ((decimal?)q["add_on_price"] is { } price) stored["add_on_price"] = Money((long)Math.Round(price * 100));
                    foreach (var o in stored["options"] as JsonArray ?? []) o!["option_id"] = (long?)o["option_id"] ?? ++_nextEtsyId;
                    // Etsy returns text HTML-encoded.
                    if ((string?)stored["instructions"] is { } instructions) stored["instructions"] = instructions.Replace("\"", "&quot;");
                    questions.Add(stored);
                }
            listing["personalization"] = new JsonObject { ["personalization_questions"] = questions };
            listing["is_personalizable"] = questions.Count > 0;
            return req.Method == HttpMethod.Post
                ? ScriptedHandler.Json(HttpStatusCode.Created, listing["personalization"]!.ToJsonString())
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (req.Method == HttpMethod.Patch && path.StartsWith($"{shop}/listings/"))
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            Writes.Add(("PATCH", path, body));
            if (FailWritesWith is { } status) return new HttpResponseMessage(status);
            var listing = ListingJson(long.Parse(parts[5]));
            foreach (var (key, value) in System.Web.HttpUtility.ParseQueryString(body) is var form
                         ? form.AllKeys.Select(k => (k!, form[k]!)) : [])
            {
                listing[key] = FormValue(key, value);
            }
            listing["last_modified_timestamp"] = (long)listing["last_modified_timestamp"]! + 10;
            return Json(listing.DeepClone());
        }
        // GET / PUT /v3/application/listings/{id}/inventory
        if (parts.Length == 5 && parts[2] == "listings" && parts[4] == "inventory" && long.TryParse(parts[3], out var invId)
            && Listings.Any(l => (long)l["listing_id"]! == invId))
        {
            var listing = ListingJson(invId);
            if (req.Method == HttpMethod.Put)
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                Writes.Add(("PUT", path, body));
                if ((FailWritesWith ?? FailInventoryWritesWith) is { } status) return new HttpResponseMessage(status);
                var update = JsonNode.Parse(body)!.AsObject();
                // Etsy stores the new table and issues new product/offering ids.
                var products = new JsonArray(update["products"]!.AsArray().Select(p => (JsonNode)new JsonObject
                {
                    ["product_id"] = ++_nextEtsyId,
                    ["sku"] = (string?)p!["sku"],
                    ["is_deleted"] = false,
                    // Etsy gives a new value an id (one per variation and text, reused across combinations).
                    ["property_values"] = new JsonArray(p["property_values"]!.AsArray().Select(v =>
                    {
                        var pv = v!.DeepClone().AsObject();
                        if (pv["value_ids"]?.AsArray().Count is null or 0 && (string?)pv["values"]?[0] is { } text)
                        {
                            var key = ((long)pv["property_id"]!, text);
                            if (!_valueIds.TryGetValue(key, out var valueId)) _valueIds[key] = valueId = ++_nextEtsyId;
                            pv["value_ids"] = new JsonArray(valueId);
                        }
                        return (JsonNode)pv;
                    }).ToArray()),
                    ["offerings"] = new JsonArray(p["offerings"]!.AsArray().Select(o => (JsonNode)new JsonObject
                    {
                        ["offering_id"] = ++_nextEtsyId,
                        ["quantity"] = (int)o!["quantity"]!,
                        ["is_enabled"] = (bool)o["is_enabled"]!,
                        ["is_deleted"] = false,
                        ["price"] = Money((long)Math.Round((decimal)o["price"]! * 100)),
                        ["readiness_state_id"] = o["readiness_state_id"]?.DeepClone(),
                    }).ToArray()),
                }).ToArray());
                listing["inventory"] = new JsonObject
                {
                    ["products"] = products,
                    ["price_on_property"] = update["price_on_property"]!.DeepClone(),
                    ["quantity_on_property"] = update["quantity_on_property"]!.DeepClone(),
                    ["sku_on_property"] = update["sku_on_property"]!.DeepClone(),
                    ["readiness_state_on_property"] = update["readiness_state_on_property"]?.DeepClone() ?? new JsonArray(),
                };
                listing["last_modified_timestamp"] = (long)listing["last_modified_timestamp"]! + 10;
                return Json(listing["inventory"]!.DeepClone());
            }
            var inventory = (JsonObject)(listing["inventory"] ?? Inventory(invId, 5)).DeepClone();
            if (query["includes"] == "Listing") inventory["listing"] = listing.DeepClone();
            return Json(inventory);
        }
        return null;
    }

    /// <summary>Listings whose getListingProperties call returns 404.</summary>
    public HashSet<long> ListingsWithoutProperties { get; } = [];

    public void Dispose()
    {
        Provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Db.Dispose();
        Storage.Dispose();
    }
}
