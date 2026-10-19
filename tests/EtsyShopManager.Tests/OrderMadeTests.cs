using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class OrderMadeTests
{
    private const long Now = 1_760_000_000;

    /// <summary>A paid, open receipt whose one item is a "Galaxy vase" in <paramref name="color"/>.</summary>
    private static JsonObject Receipt(long id, int quantity = 1, string color = "Blue", long shipBy = Now + 86400, long updated = Now) => new()
    {
        ["receipt_id"] = id,
        ["buyer_user_id"] = 500 + id,
        ["name"] = $"Buyer {id}",
        ["status"] = "Paid",
        ["is_paid"] = true,
        ["is_shipped"] = false,
        ["created_timestamp"] = Now - 1000 + id,
        ["updated_timestamp"] = updated,
        ["grandtotal"] = FakeEtsyShop.Money(1999),
        ["subtotal"] = FakeEtsyShop.Money(1999),
        ["transactions"] = new JsonArray(new JsonObject
        {
            ["transaction_id"] = id * 10,
            ["receipt_id"] = id,
            ["buyer_user_id"] = 500 + id,
            ["title"] = "Galaxy vase",
            ["listing_id"] = 1,
            ["listing_image_id"] = 11,
            ["quantity"] = quantity,
            ["price"] = FakeEtsyShop.Money(1999),
            ["expected_ship_date"] = shipBy,
            ["variations"] = new JsonArray(new JsonObject
            {
                ["property_id"] = 200, ["value_id"] = 1, ["formatted_name"] = "Color", ["formatted_value"] = color,
            }),
        }),
    };

    private static async Task<(FakeEtsyShop Shop, OrderMadeService Made, OrderQueries Queries)> PolledAsync(params JsonObject[] receipts)
    {
        var shop = new FakeEtsyShop();
        shop.Receipts.AddRange(receipts);
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        return (shop, new OrderMadeService(shop.Db), shop.Provider.GetRequiredService<OrderQueries>());
    }

    [Fact]
    public async Task Marking_a_To_make_row_marks_the_soonest_ship_by_items_and_the_order_cards_show_it()
    {
        // Two orders for the same blue vase (the later one ships first) and one for a red one.
        var (shop, made, queries) = await PolledAsync(
            Receipt(1, quantity: 2, shipBy: Now + 3 * 86400),
            Receipt(2, quantity: 1, shipBy: Now + 86400),
            Receipt(3, color: "Red"));
        using var _ = shop;

        var rows = ToMakeList.Build(await queries.GetOpenOrdersAsync(null), TimeZoneInfo.Utc);
        var blue = Assert.Single(rows, r => r.Row.ToJsonString().Contains("Blue"));
        Assert.Equal(3, blue.Quantity);
        Assert.Equal([2L, 1L], blue.Items.Select(i => i.EtsyTransactionId / 10));

        await made.SetMadeAsync(blue.Items, 2);

        // Saved per order item: the order shipping first is made, the other has one of two made.
        var open = await queries.GetOpenOrdersAsync(null);
        Assert.Equal(1, Item(open, 2).UnitsMade);
        Assert.Equal(1, Item(open, 1).UnitsMade);
        Assert.Equal(0, Item(open, 3).UnitsMade);

        rows = ToMakeList.Build(open, TimeZoneInfo.Utc);
        blue = Assert.Single(rows, r => r.Row.ToJsonString().Contains("Blue"));
        Assert.Equal((2, 1, false), (blue.Made, blue.Left, blue.IsMade));
        Assert.False(Assert.Single(rows, r => r.Row.ToJsonString().Contains("Red")).IsMade);

        // And the other way: marking the rest made on the order card completes the row.
        await made.SetMadeAsync([Item(open, 1)], 2);
        rows = ToMakeList.Build(await queries.GetOpenOrdersAsync(null), TimeZoneInfo.Utc);
        Assert.True(Assert.Single(rows, r => r.Row.ToJsonString().Contains("Blue")).IsMade);

        // Unmarking the row clears every item behind it.
        await made.SetMadeAsync(rows.Single(r => r.IsMade).Items, 0);
        open = await queries.GetOpenOrdersAsync(null);
        Assert.All(open.SelectMany(o => o.Items), i => Assert.Equal(0, i.MadeQuantity));
    }

    [Fact]
    public async Task Made_marks_survive_order_polls_and_stay_in_the_app()
    {
        var (shop, made, queries) = await PolledAsync(Receipt(1, quantity: 2));
        using var _ = shop;
        var item = Item(await queries.GetOpenOrdersAsync(null), 1);
        shop.Api.Requests.Clear();

        await made.SetMadeAsync([item], 1);

        // Nothing is sent to Etsy.
        Assert.Empty(shop.Api.Requests);

        // Etsy changes the receipt (a new message, say): the next poll updates the order but keeps what's made.
        shop.Receipts[0] = Receipt(1, quantity: 2, updated: shop.Time.Now.ToUnixTimeSeconds() + 60);
        shop.Receipts[0]["message_from_buyer"] = "Can it ship Friday?";
        await shop.Provider.GetRequiredService<OrderPoller>().PollShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        var order = Assert.Single(await queries.GetOpenOrdersAsync(null));
        Assert.Equal("Can it ship Friday?", order.MessageFromBuyer);
        Assert.Equal(1, order.Items.Single().MadeQuantity);
    }

    [Fact]
    public async Task Open_pages_hear_about_changes_with_the_page_that_made_them()
    {
        var (shop, made, queries) = await PolledAsync(Receipt(1));
        using var _ = shop;
        var heard = new List<object?>();
        made.Changed += heard.Add;
        made.Changed += _ => throw new ObjectDisposedException("a closed page");
        var page = new object();

        await made.SetMadeAsync([Item(await queries.GetOpenOrdersAsync(null), 1)], 1, source: page);

        Assert.Same(page, Assert.Single(heard));
        using var db = shop.Db.CreateDbContext();
        Assert.Equal(1, (await db.OrderItems.SingleAsync()).MadeQuantity);
    }

    [Fact]
    public void Made_units_are_spread_in_order_and_never_exceed_the_quantity()
    {
        OrderItem[] items = [new() { Quantity = 2 }, new() { Quantity = 0 }, new() { Quantity = 3 }];

        OrderMadeService.Distribute(items, 4);
        Assert.Equal([2, 1, 1], items.Select(i => i.MadeQuantity));

        OrderMadeService.Distribute(items, 99);
        Assert.Equal([2, 1, 3], items.Select(i => i.MadeQuantity));

        OrderMadeService.Distribute(items, -1);
        Assert.All(items, i => Assert.Equal(0, i.MadeQuantity));

        // Etsy's quantity dropped after marking: what's made is capped to it.
        var item = new OrderItem { Quantity = 1, MadeQuantity = 3 };
        Assert.Equal(1, item.UnitsMade);
    }

    [Fact]
    public async Task The_Home_Assistant_payload_carries_made_units_and_still_counts_every_item()
    {
        var (shop, made, queries) = await PolledAsync(Receipt(1, quantity: 2));
        using var _ = shop;
        await made.SetMadeAsync([Item(await queries.GetOpenOrdersAsync(null), 1)], 2);

        var payload = HaPayloadBuilder.OpenOrders(await queries.GetOpenOrdersAsync(null), TimeZoneInfo.Utc);

        Assert.Equal(2, (int)payload["total_items_needed"]!);
        var item = payload["open_orders"]![0]!["items"]![0]!;
        Assert.Equal(("10", 2), ((string?)item["transaction_id"], (int)item["made_quantity"]!));
    }

    [Fact]
    public async Task The_card_marks_an_item_by_receipt_and_transaction_and_open_pages_hear_it()
    {
        var (shop, made, queries) = await PolledAsync(Receipt(1, quantity: 2), Receipt(2));
        using var _ = shop;
        var heard = 0;
        made.Changed += _ => heard++;
        shop.Api.Requests.Clear();

        Assert.Equal(2, await made.SetMadeAsync(1, 10, 5)); // capped to the quantity
        Assert.Equal(0, await made.SetMadeAsync(2, 20, -1));
        Assert.Null(await made.SetMadeAsync(1, 20, 1)); // transaction 20 belongs to receipt 2
        Assert.Null(await made.SetMadeAsync(9, 90, 1));

        var open = await queries.GetOpenOrdersAsync(null);
        Assert.Equal((2, 0), (Item(open, 1).MadeQuantity, Item(open, 2).MadeQuantity));
        Assert.Equal(2, heard);
        Assert.Empty(shop.Api.Requests); // nothing goes to Etsy
    }

    private static OrderItem Item(IEnumerable<Order> orders, long receiptId) =>
        orders.Single(o => o.EtsyReceiptId == receiptId).Items.Single();
}
