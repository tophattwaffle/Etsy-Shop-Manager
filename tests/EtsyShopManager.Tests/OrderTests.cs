using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class HaPayloadTests
{
    private static readonly Dictionary<long, string> ImageMap = new()
    {
        [7001] = "https://i.etsystatic.com/7001/il_170x135.jpg",
        [7002] = "https://i.etsystatic.com/7002/il_170x135.jpg",
    };

    private static List<Order> FixtureOrders()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "receipts.json"));
        return JsonNode.Parse(json)!.AsArray().Select(node =>
        {
            var order = new Order();
            OrderMapper.Apply(order, node.Deserialize<EtsyReceipt>(EtsyJson.Options)!, node!.ToJsonString(), DateTime.UtcNow);
            foreach (var item in order.Items)
                item.ImageUrl = item.EtsyListingId is { } id ? ImageMap.GetValueOrDefault(id) : null;
            return order;
        }).ToList();
    }

    /// <summary>
    /// open-orders.expected.json was produced by running the old etsyapp integration's own Python
    /// (build_receipt_summary, _is_open_order, _aggregate_items_needed) on receipts.json, with dates in UTC.
    /// The card reads this shape, so the port must match it field for field. Fields the app added since (made_quantity)
    /// are in the expected file too.
    /// </summary>
    [Fact]
    public void Open_orders_payload_matches_the_old_integration()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "open-orders.expected.json")))!;
        expected["address_alerts"] = new JsonArray();
        expected["address_alert_css"] = HaPayloadBuilder.AddressAlertCss;

        var actual = HaPayloadBuilder.OpenOrders(FixtureOrders(), TimeZoneInfo.Utc);

        AssertJsonEquivalent(expected, actual, "$");
    }

    [Fact]
    public void Open_means_paid_unshipped_and_not_resolved()
    {
        var orders = FixtureOrders();
        Assert.Equal([3001L, 3002L], orders.Where(o => o.IsOpen).Select(o => o.EtsyReceiptId).Order());
        Assert.False(orders.Single(o => o.EtsyReceiptId == 3004).IsOpen); // canceled before shipping
    }

    [Fact]
    public void Mapper_keeps_money_dates_and_ship_by()
    {
        var order = FixtureOrders().Single(o => o.EtsyReceiptId == 3001);
        Assert.Equal(5398, order.Grandtotal);
        Assert.Equal("USD", order.CurrencyCode);
        Assert.Equal("Alice Example", order.BuyerName);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1760700000), order.ShipByUtc); // earliest item
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(3, order.Items.Single(i => i.EtsyTransactionId == 9001).Variations.Count);
    }

    private static void AssertJsonEquivalent(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{path}: expected null, got {actual?.ToJsonString()}");
                break;
            case JsonObject e:
                var a = Assert.IsType<JsonObject>(actual);
                Assert.True(e.Select(p => p.Key).Order().SequenceEqual(a.Select(p => p.Key).Order()),
                    $"{path}: keys differ. Expected [{string.Join(", ", e.Select(p => p.Key).Order())}], got [{string.Join(", ", a.Select(p => p.Key).Order())}]");
                foreach (var (key, value) in e) AssertJsonEquivalent(value, a[key], $"{path}.{key}");
                break;
            case JsonArray e:
                var arr = Assert.IsType<JsonArray>(actual);
                Assert.True(e.Count == arr.Count, $"{path}: expected {e.Count} items, got {arr.Count}");
                for (var i = 0; i < e.Count; i++) AssertJsonEquivalent(e[i], arr[i], $"{path}[{i}]");
                break;
            default:
                var ev = expected.AsValue();
                Assert.True(actual is JsonValue, $"{path}: expected a value, got {actual?.ToJsonString()}");
                var av = actual!.AsValue();
                if (ev.GetValueKind() == JsonValueKind.Number)
                    Assert.True(av.GetValueKind() == JsonValueKind.Number && ev.GetValue<decimal>() == decimal.Parse(av.ToJsonString()),
                        $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}");
                else
                    Assert.True(ev.ToJsonString() == av.ToJsonString(), $"{path}: expected {ev.ToJsonString()}, got {av.ToJsonString()}");
                break;
        }
    }
}

public class OrderPollerTests
{
    private static JsonObject Receipt(long id, long updated, bool shipped = false, string status = "Paid", long listingId = 1,
        long? imageId = 11, long? created = null) => new()
    {
        ["receipt_id"] = id,
        ["buyer_user_id"] = 500 + id,
        ["name"] = $"Buyer {id}",
        ["status"] = status,
        ["is_paid"] = true,
        ["is_shipped"] = shipped,
        ["created_timestamp"] = created ?? updated - 100,
        ["updated_timestamp"] = updated,
        ["grandtotal"] = FakeEtsyShop.Money(1999),
        ["subtotal"] = FakeEtsyShop.Money(1999),
        ["transactions"] = new JsonArray(new JsonObject
        {
            ["transaction_id"] = id * 10,
            ["receipt_id"] = id,
            ["buyer_user_id"] = 500 + id,
            ["title"] = "Galaxy vase",
            ["listing_id"] = listingId,
            ["listing_image_id"] = imageId,
            ["quantity"] = 1,
            ["price"] = FakeEtsyShop.Money(1999),
            ["expected_ship_date"] = updated + 86400,
            ["variations"] = new JsonArray(),
        }),
    };

    [Fact]
    public async Task Backfill_pages_through_history_without_events_then_new_orders_raise_OrderReceived()
    {
        using var shop = new FakeEtsyShop();
        var poller = shop.Provider.GetRequiredService<OrderPoller>();
        poller.PageSize = 2;
        var now = shop.Time.Now.ToUnixTimeSeconds();
        for (var i = 1; i <= 5; i++) shop.Receipts.Add(Receipt(i, now - 10_000 + i));

        var first = await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.True(first.WasBackfill);
        Assert.Equal(5, first.New);
        Assert.Empty(shop.Events.Events);
        Assert.Equal(3, shop.RequestedPaths.Count(p => p.Contains("/receipts")));

        shop.Receipts.Add(Receipt(6, now + 60));
        shop.Api.Requests.Clear();
        var second = await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.False(second.WasBackfill);
        Assert.Equal(1, second.New);
        var received = Assert.Single(shop.Events.Events.OfType<OrderReceived>());
        Assert.Equal(6, received.EtsyReceiptId);
        // Asks only for receipts changed since the last poll started (minus the overlap).
        var since = shop.Time.Now.UtcDateTime - OrderPoller.Overlap;
        Assert.True(shop.RequestedPaths.Any(p => p.Contains($"min_last_modified={new DateTimeOffset(since).ToUnixTimeSeconds()}")),
            string.Join(" | ", shop.RequestedPaths));
    }

    [Fact]
    public async Task A_receipt_modified_mid_poll_does_not_make_another_one_skipped()
    {
        using var shop = new FakeEtsyShop();
        var poller = shop.Provider.GetRequiredService<OrderPoller>();
        poller.PageSize = 2;
        for (var i = 1; i <= 5; i++) shop.Receipts.Add(Receipt(i, 1_760_000_000 + i));
        // After the first page, receipt 1 is shipped on Etsy: its update time jumps past all the others.
        shop.AfterReceiptsPage = offset =>
        {
            if (offset == 0) shop.Receipts[0] = Receipt(1, 1_760_009_999, shipped: true, status: "Completed", created: 1_759_999_901);
        };

        await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        Assert.Equal([1L, 2L, 3L, 4L, 5L], await db.Orders.OrderBy(o => o.EtsyReceiptId).Select(o => o.EtsyReceiptId).ToListAsync());
        Assert.All(shop.RequestedPaths.Where(p => p.Contains("/receipts")), p => Assert.Contains("sort_on=created", p));
    }

    [Fact]
    public async Task Repolling_is_idempotent_and_picks_up_status_changes()
    {
        using var shop = new FakeEtsyShop();
        var poller = shop.Provider.GetRequiredService<OrderPoller>();
        var now = shop.Time.Now.ToUnixTimeSeconds();
        shop.Receipts.Add(Receipt(1, now - 1000, created: now - 2000));
        await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        int orderId, itemId;
        await using (var db = shop.Db.CreateDbContext())
        {
            var o = await db.Orders.Include(x => x.Items).SingleAsync();
            (orderId, itemId) = (o.Id, o.Items.Single().Id);
            Assert.True(o.IsOpen);
        }

        await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None); // overlap re-reads it: no change
        shop.Receipts[0] = Receipt(1, now + 500, shipped: true, status: "Completed", created: now - 2000);
        await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var o = await db.Orders.Include(x => x.Items).SingleAsync();
            Assert.Equal((orderId, itemId), (o.Id, o.Items.Single().Id));
            Assert.False(o.IsOpen);
            Assert.Equal(1, await db.OrderItems.CountAsync());
        }
        Assert.Empty(shop.Events.Events);
    }

    [Fact]
    public async Task Interrupted_backfill_resumes_from_the_saved_offset()
    {
        using var shop = new FakeEtsyShop();
        var poller = shop.Provider.GetRequiredService<OrderPoller>();
        poller.PageSize = 2;
        for (var i = 1; i <= 5; i++) shop.Receipts.Add(Receipt(i, 1_760_000_000 + i));
        shop.FailingReceiptOffsets.Add(4);

        await Assert.ThrowsAsync<EtsyApiException>(() => poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None));
        await using (var db = shop.Db.CreateDbContext())
        {
            Assert.Equal(4, await db.Orders.CountAsync());
            Assert.Equal(4, (await db.ShopOrderSyncStates.SingleAsync()).Offset);
        }

        shop.FailingReceiptOffsets.Clear();
        shop.Api.Requests.Clear();
        var result = await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.True(result.WasBackfill);
        Assert.All(shop.RequestedPaths.Where(p => p.Contains("/receipts")), p => Assert.Contains("offset=4", p));
        await using (var db = shop.Db.CreateDbContext())
        {
            Assert.Equal(5, await db.Orders.CountAsync());
            var sync = await db.ShopOrderSyncStates.SingleAsync();
            Assert.Equal(0, sync.Offset);
            Assert.NotNull(sync.BackfillCompletedAtUtc);
        }
        Assert.Empty(shop.Events.Events); // still the backfill
    }

    [Fact]
    public async Task Item_photos_come_from_imported_listings_or_one_batch_call()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Imported"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var gone = FakeEtsyShop.Listing(9, "Deleted from shop");
        shop.BatchOnlyListings.Add(gone);
        shop.Receipts.Add(Receipt(1, 1_760_000_000, listingId: 1, imageId: 12)); // second photo of listing 1
        shop.Receipts.Add(Receipt(2, 1_760_000_001, listingId: 9, imageId: null));
        shop.Api.Requests.Clear();

        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var items = await db.OrderItems.ToDictionaryAsync(i => i.EtsyListingId!.Value);
        Assert.Equal("https://i.etsystatic.com/12/il_570xN.jpg", items[1].ImageUrl);
        Assert.Equal("https://i.etsystatic.com/91/il_570xN.jpg", items[9].ImageUrl); // rank 1 of the batch result
        var batch = Assert.Single(shop.RequestedPaths, p => p.StartsWith("/v3/application/listings/batch"));
        Assert.Contains("listing_ids=9", batch);
    }

    [Fact]
    public async Task Item_photo_skips_a_listing_photo_that_is_gone_from_Etsy()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Imported"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using (var db = shop.Db.CreateDbContext())
            await db.ListingImages.Where(i => i.EtsyImageId == 12)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DownloadError, "Etsy no longer has this image (404)"));
        shop.Receipts.Add(Receipt(1, shop.Time.Now.ToUnixTimeSeconds(), listingId: 1, imageId: 12));

        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using var check = shop.Db.CreateDbContext();
        Assert.Equal("https://i.etsystatic.com/11/il_570xN.jpg", (await check.OrderItems.SingleAsync()).ImageUrl);
    }

    [Fact]
    public async Task HA_open_orders_lists_each_shop_with_the_card_fields()
    {
        using var shop = new FakeEtsyShop();
        shop.Receipts.Add(Receipt(1, 1_760_000_000));
        shop.Receipts.Add(Receipt(2, 1_760_000_001, shipped: true, status: "Completed"));
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        var queries = shop.Provider.GetRequiredService<OrderQueries>();

        var all = await queries.HaOpenOrdersAllAsync();
        var payload = Assert.Single(all["shops"]!.AsArray())!.AsObject();

        Assert.Equal("42", (string?)payload["shop_id"]);
        Assert.Equal("TestShop", (string?)payload["shop_name"]);
        Assert.Equal(1, (int)payload["open_orders_count"]!);
        Assert.Equal(1, (int)payload["total_items_needed"]!);
        var order = payload["open_orders"]![0]!;
        Assert.Equal("1", (string?)order["receipt_id"]);
        Assert.Equal("Buyer 1", (string?)order["buyer_name"]);
        Assert.Equal(1_760_086_400L, (long)order["ship_by_ts"]!);
        Assert.Equal(19.99, (double)order["order_total"]!);
        Assert.Equal("Galaxy vase", (string?)order["items"]![0]!["title"]);
        Assert.NotNull(await queries.HaOpenOrdersAsync(42));
        Assert.Null(await queries.HaOpenOrdersAsync(43));
    }

    private static JsonObject Gift(long id, long updated)
    {
        var receipt = Receipt(id, updated);
        receipt["is_gift"] = true;
        receipt["gift_message"] = "Happy birthday!";
        receipt["gift_sender"] = ""; // as Etsy sends it
        return receipt;
    }

    private static async Task<string?> StoredSenderAsync(FakeEtsyShop shop, long receiptId)
    {
        await using var db = shop.Db.CreateDbContext();
        return OrderPrintService.GiftSender(await db.Orders.SingleAsync(o => o.EtsyReceiptId == receiptId));
    }

    private static async Task GrantBuyerNamesAsync(FakeEtsyShop shop)
    {
        await using var db = shop.Db.CreateDbContext();
        await db.ShopTokens.ExecuteUpdateAsync(t => t.SetProperty(x => x.Scopes, $"listings_r transactions_r {OrderPoller.BuyerNameScope}"));
    }

    private static List<string> UserCalls(FakeEtsyShop shop) => [.. shop.RequestedPaths.Where(p => p.Contains("/users/"))];

    [Fact]
    public async Task Gift_orders_get_the_buyers_first_name_once_per_buyer()
    {
        using var shop = new FakeEtsyShop();
        await GrantBuyerNamesAsync(shop);
        var now = shop.Time.Now.ToUnixTimeSeconds();
        shop.Receipts.Add(Gift(1, now - 100));                   // buyer 501
        shop.Receipts.Add(Gift(2, now - 90));                    // buyer 502, whom Etsy won't show (404)
        shop.Receipts.Add(Receipt(3, now - 80, shipped: true, status: "Completed")); // not a gift, and shipped
        var repeat = Gift(4, now - 70);
        repeat["buyer_user_id"] = 501;                           // the same buyer again
        shop.Receipts.Add(repeat);
        shop.BuyerFirstNames[501] = " Kate ";

        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.Equal("Kate", await StoredSenderAsync(shop, 1));
        Assert.Equal("Kate", await StoredSenderAsync(shop, 4));
        Assert.Null(await StoredSenderAsync(shop, 2));
        Assert.Equal(["/v3/application/users/501", "/v3/application/users/502"], UserCalls(shop));
        Assert.Empty(shop.Writes);

        // Seen again, the named buyer isn't looked up again; the one Etsy wouldn't show is tried again.
        shop.Receipts[0] = Gift(1, now + 60);
        shop.Api.Requests.Clear();
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        Assert.Equal(["/v3/application/users/502"], UserCalls(shop));
    }

    [Fact]
    public async Task Open_orders_get_the_buyers_profile_name_and_shipped_ones_are_not_looked_up()
    {
        using var shop = new FakeEtsyShop();
        await GrantBuyerNamesAsync(shop);
        var now = shop.Time.Now.ToUnixTimeSeconds();
        shop.Receipts.Add(Receipt(1, now - 100));                                         // open: buyer 501
        shop.Receipts.Add(Receipt(2, now - 90, shipped: true, status: "Completed"));      // shipped: not needed
        shop.Receipts.Add(Receipt(3, now - 80));                                          // open: buyer 503, account closed (404)
        shop.BuyerFirstNames[501] = "Meg";
        shop.BuyerLastNames[501] = "Taylor";

        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var orders = await db.Orders.OrderBy(o => o.EtsyReceiptId).ToListAsync();
        Assert.Equal(["Meg Taylor", null, null], orders.Select(o => o.BuyerProfileName));
        Assert.Equal(["/v3/application/users/501", "/v3/application/users/503"], UserCalls(shop));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Buyer_names_need_the_shop_reconnected_with_the_scope_then_the_job_fills_older_gift_orders()
    {
        using var shop = new FakeEtsyShop(); // token from before email_r was asked for
        var now = shop.Time.Now.ToUnixTimeSeconds();
        shop.Receipts.Add(Gift(1, now - 100));
        shop.Receipts.Add(Receipt(3, now - 80, shipped: true, status: "Completed"));
        shop.BuyerFirstNames[501] = "Kate";
        var poller = shop.Provider.GetRequiredService<OrderPoller>();
        await poller.PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        Assert.Null(await StoredSenderAsync(shop, 1));
        Assert.Empty(UserCalls(shop)); // nothing is asked without the scope

        var job = new GiftBuyerNamesJob(shop.Db, poller);
        var context = new JobExecutionContext { JobId = 1, JobType = GiftBuyerNamesJob.Type, Attempt = 1, PayloadJson = "{}" };
        await job.HandleAsync(context, CancellationToken.None);
        Assert.Equal("0 of 1 order(s) named; reconnect TestShop to allow buyer names", context.Result);

        // Reconnecting grants the scope and queues the job, which names the older order.
        await GrantBuyerNamesAsync(shop);
        var queue = new RecordingJobQueue();
        await new GiftBuyerNamesOnShopConnected(queue).HandleAsync(
            new ShopConnected(FakeEtsyShop.ShopId, FakeEtsyShop.EtsyShopId, "TestShop", IsReconnect: true), CancellationToken.None);
        Assert.Equal(GiftBuyerNamesJob.Type, Assert.Single(queue.Requests).JobType);
        await job.HandleAsync(context, CancellationToken.None);
        Assert.Equal("1 of 1 order(s) named", context.Result);
        Assert.Equal("Kate", await StoredSenderAsync(shop, 1));

        await job.HandleAsync(context, CancellationToken.None);
        Assert.Equal("every open and gift order has a buyer name", context.Result);
        Assert.Single(UserCalls(shop));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Startup_queues_buyer_names_and_shop_refreshes_only_when_something_is_missing()
    {
        using var shop = new FakeEtsyShop();
        var queue = shop.Provider.GetRequiredService<RecordingJobQueue>();
        var shops = ActivatorUtilities.CreateInstance<EtsyShopManager.Web.Modules.Shops.ShopService>(shop.Provider);
        var now = shop.Time.Now.ToUnixTimeSeconds();
        shop.Receipts.Add(Gift(1, now - 100));
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        queue.Requests.Clear();

        async Task StartAsync()
        {
            await new EtsyShopManager.Web.Modules.Shops.ShopDetailsStartup(shop.Db, shops,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EtsyShopManager.Web.Modules.Shops.ShopDetailsStartup>.Instance).StartAsync(CancellationToken.None);
            await new GiftBuyerNamesStartup(shop.Db, queue,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GiftBuyerNamesStartup>.Instance).StartAsync(CancellationToken.None);
        }

        // The shop's sales count was never read and a gift order has no buyer name: both are queued.
        await StartAsync();
        Assert.Equal([EtsyShopManager.Web.Modules.Shops.ShopRefreshJob.Type, GiftBuyerNamesJob.Type], queue.Requests.Select(r => r.JobType));

        queue.Requests.Clear();
        await using (var db = shop.Db.CreateDbContext())
        {
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.TransactionSoldCount, 12));
            await db.Orders.ExecuteUpdateAsync(s => s.SetProperty(x => x.GiftFromName, "Kate").SetProperty(x => x.BuyerProfileName, "Kate Sample"));
        }
        await StartAsync();
        Assert.Empty(queue.Requests);
    }

    /// <summary>A gift receipt as Etsy sends it: its text HTML-encoded (an apostrophe as &amp;#39;).</summary>
    private static JsonObject EncodedGift(long id, long updated)
    {
        var receipt = Gift(id, updated);
        receipt["name"] = "Pat O&#39;Brien";
        receipt["gift_message"] = "Happy Mother&#39;s Day &amp; love you!";
        receipt["message_from_buyer"] = "It&#39;s a surprise";
        var transaction = (JsonObject)((JsonArray)receipt["transactions"]!)[0]!;
        transaction["title"] = "Kid&#39;s &quot;Galaxy&quot; vase";
        transaction["variations"] = new JsonArray(new JsonObject
        {
            ["property_id"] = 54, ["value_id"] = null, ["formatted_name"] = "Personalization", ["formatted_value"] = "Dad&#39;s",
        });
        return receipt;
    }

    [Fact]
    public async Task Etsys_encoded_receipt_text_is_stored_as_plain_text()
    {
        using var shop = new FakeEtsyShop();
        shop.Receipts.Add(EncodedGift(1, shop.Time.Now.ToUnixTimeSeconds() - 100));

        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(("Pat O'Brien", "Happy Mother's Day & love you!", "It's a surprise"), (order.BuyerName, order.GiftMessage, order.MessageFromBuyer));
        var item = Assert.Single(order.Items);
        Assert.Equal("Kid's \"Galaxy\" vase", item.Title);
        Assert.Equal("Dad's", Assert.Single(item.Variations).Value);
        // What the slips read straight from the stored receipt is decoded too.
        Assert.Equal("Pat O'Brien", OrderPrintService.Address(order).Name);
    }

    [Fact]
    public async Task Orders_stored_before_the_fix_are_decoded_once()
    {
        using var shop = new FakeEtsyShop();
        shop.Receipts.Add(EncodedGift(1, shop.Time.Now.ToUnixTimeSeconds() - 100));
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        await using (var db = shop.Db.CreateDbContext()) // as an older version stored it
        {
            var order = await db.Orders.Include(o => o.Items).SingleAsync();
            (order.BuyerName, order.GiftMessage) = ("Pat O&#39;Brien", "Happy Mother&#39;s Day &amp; love you!");
            order.Items[0].Title = "Kid&#39;s vase";
            order.Items[0].Variations = [order.Items[0].Variations[0] with { Value = "Dad&#39;s" }];
            await db.SaveChangesAsync();
        }
        var settings = shop.Provider.GetRequiredService<EtsyShopManager.Core.Services.ISettingsStore>();
        var queue = new RecordingJobQueue();
        var startup = new OrderTextRepairStartup(queue, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderTextRepairStartup>.Instance);
        await startup.StartAsync(CancellationToken.None);
        Assert.Equal(OrderTextRepairJob.Type, Assert.Single(queue.Requests).JobType);

        var context = new JobExecutionContext { JobId = 1, JobType = OrderTextRepairJob.Type, Attempt = 1, PayloadJson = "{}" };
        await new OrderTextRepairJob(shop.Db, settings).HandleAsync(context, CancellationToken.None);

        Assert.Equal("1 order(s) decoded", context.Result);
        await using (var db = shop.Db.CreateDbContext())
        {
            var order = await db.Orders.Include(o => o.Items).SingleAsync();
            Assert.Equal(("Pat O'Brien", "Happy Mother's Day & love you!"), (order.BuyerName, order.GiftMessage));
            Assert.Equal(("Kid's vase", "Dad's"), (order.Items[0].Title, order.Items[0].Variations[0].Value));
        }
        queue.Requests.Clear();
        await startup.StartAsync(CancellationToken.None);
        Assert.Empty(queue.Requests); // runs once
        Assert.Empty(shop.Writes);
    }
}

public class ApiKeyTests
{
    [Fact]
    public async Task Key_is_shown_once_stored_hashed_validates_and_can_be_revoked()
    {
        using var shop = new FakeEtsyShop();
        var keys = shop.Provider.GetRequiredService<ApiKeyService>();

        var (info, key) = await keys.CreateAsync("Home Assistant", "tophattwaffle");

        Assert.StartsWith(ApiKeyService.KeyPrefix, key);
        Assert.StartsWith(info.Prefix, key);
        await using (var db = shop.Db.CreateDbContext())
        {
            var stored = await db.ApiKeys.SingleAsync();
            Assert.Equal(ApiKeyService.Hash(key), stored.Hash);
            Assert.DoesNotContain(key, JsonSerializer.Serialize(stored));
        }
        Assert.True(await keys.ValidateAsync(key));
        Assert.False(await keys.ValidateAsync(key + "x"));
        Assert.False(await keys.ValidateAsync(null));

        await keys.RevokeAsync(info.Id, "tophattwaffle");
        Assert.False(await keys.ValidateAsync(key));
    }

    [Theory]
    [InlineData("Authorization", "Bearer {key}", 200)]
    [InlineData("X-Api-Key", "{key}", 200)]
    [InlineData("Authorization", "Bearer esm_wrong", 401)]
    [InlineData(null, null, 401)]
    public async Task Endpoint_filter_accepts_bearer_or_header(string? header, string? value, int expectedStatus)
    {
        using var shop = new FakeEtsyShop();
        var keys = shop.Provider.GetRequiredService<ApiKeyService>();
        var (_, key) = await keys.CreateAsync("HA", null);
        var http = new DefaultHttpContext { RequestServices = shop.Provider };
        if (header is not null) http.Request.Headers[header] = value!.Replace("{key}", key);

        var filter = new ApiKeyEndpointFilter(keys);
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http),
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}

public class RecurringIntervalTests
{
    private static readonly RecurringJobDefinition Def = new("Poll", "x", TimeSpan.FromSeconds(300))
    {
        IntervalSettingKey = "k",
        MinInterval = TimeSpan.FromSeconds(60),
    };

    [Theory]
    [InlineData(null, 300)]
    [InlineData("", 300)]
    [InlineData("abc", 300)]
    [InlineData("120", 120)]
    [InlineData("10", 60)] // raised to the minimum
    [InlineData("0", 300)]
    public void Setting_overrides_interval(string? setting, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Def.EffectiveInterval(setting));
}

public class JobCleanupTests
{
    [Fact]
    public async Task Routine_scheduled_successes_go_after_a_day_everything_else_is_kept_longer()
    {
        using var db = new TestDb();
        var time = new ImmediateTimeProvider();
        var now = time.Now.UtcDateTime;
        Job Make(string type, JobStatus status, string? by, double hoursAgo) => new()
        {
            JobType = type, Status = status, CreatedBy = by, CreatedAtUtc = now.AddHours(-hoursAgo),
            RunAfterUtc = now.AddHours(-hoursAgo), CompletedAtUtc = now.AddHours(-hoursAgo),
        };
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Jobs.AddRange(
                Make("poll", JobStatus.Succeeded, "scheduler", 30),    // deleted
                Make("poll", JobStatus.Succeeded, "scheduler", 2),     // kept: recent
                Make("poll", JobStatus.Failed, "scheduler", 30),       // kept: failures stay
                Make("import", JobStatus.Succeeded, "tophattwaffle", 30));      // kept: not routine
            await ctx.SaveChangesAsync();
        }

        await new EtsyShopManager.Web.Modules.JobsCleanupJob(db,
                Microsoft.Extensions.Options.Options.Create(new EtsyShopManager.Web.Infrastructure.Jobs.JobOptions()), time)
            .HandleAsync(new JobExecutionContext { JobId = 1, JobType = "jobs.cleanup", Attempt = 1 }, CancellationToken.None);

        await using var check = db.CreateDbContext();
        Assert.Equal(3, await check.Jobs.CountAsync());
        Assert.DoesNotContain(await check.Jobs.ToListAsync(), j => j.Status == JobStatus.Succeeded && j.CreatedBy == "scheduler" && j.CompletedAtUtc < now.AddHours(-24));
    }
}

public class OrderDueTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(96 * 60, "4d 0h", false, "--due-hue: 120")]      // at or past green_hours: fully green
    [InlineData(72 * 60 + 5, "3d 0h", false, "--due-hue: 120")]
    [InlineData(42 * 60 + 30, "1d 18h", false, "--due-hue: 61")]   // halfway between 12 h and 72 h: yellow
    [InlineData(13 * 60 + 15, "13h 15m", false, "--due-hue: 3")]
    [InlineData(40, "40m", true, "--due-hue: 0")]                  // under red_hours: red and urgent
    [InlineData(-(5 * 60 + 12), "5h 12m late", true, "--due-hue: 0")]
    [InlineData(-(26 * 60), "1d 2h late", true, "--due-hue: 0")]
    public void Ship_by_pill_matches_the_HA_card(int minutes, string text, bool urgent, string style)
    {
        Assert.Equal((text, urgent, style), OrderDue.For(Now.AddMinutes(minutes), Now));
    }

    [Fact]
    public void Ship_by_pill_without_a_date_is_a_dash()
    {
        Assert.Equal(("—", false, (string?)null), OrderDue.For(null, Now));
    }

    [Theory]
    [InlineData("completed", true, true, "Shipped", "--due-hue: 120")]
    [InlineData("completed", true, false, "Completed", "--due-hue: 120")]
    [InlineData("paid", true, false, "Open", "--due-hue: 45")]
    [InlineData("Canceled", true, false, "Canceled", "--due-hue: 0")]
    [InlineData("fully refunded", true, true, "Refunded", "--due-hue: 0")]
    [InlineData("partially refunded", true, true, "Partly refunded", "--due-hue: 30")]
    [InlineData("payment processing", false, false, "Payment processing", null)]
    public void History_status_pill_uses_the_ship_by_pill_style(string status, bool paid, bool shipped, string text, string? style)
    {
        var order = new Order { Status = status, IsPaid = paid, IsShipped = shipped };
        Assert.Equal((text, style), OrderDue.Status(order));
    }
}
