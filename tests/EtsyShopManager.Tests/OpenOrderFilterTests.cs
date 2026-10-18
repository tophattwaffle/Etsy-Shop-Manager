using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

/// <summary>Sort and filters of the Open orders tab, with made-up sample orders (no customer data).</summary>
public class OpenOrderFilterTests
{
    private static Order Sample(long receipt, int createdDay, int? shipByDay, string? country = null, string? shipping = null,
        string? note = null, bool gift = false, string? personalization = null)
    {
        var shippingJson = shipping is null ? "null" : $"\"{shipping}\"";
        var order = new Order
        {
            Id = (int)receipt, ShopId = 1, EtsyReceiptId = receipt, IsPaid = true, Status = "paid", IsGift = gift, MessageFromBuyer = note,
            EtsyCreatedAtUtc = new DateTime(2026, 10, createdDay, 12, 0, 0, DateTimeKind.Utc),
            ShipByUtc = shipByDay is { } d ? new DateTime(2026, 10, d, 0, 0, 0, DateTimeKind.Utc) : null,
            RawJson = $$"""{"country_iso":{{(country is null ? "null" : $"\"{country}\"")}},"transactions":[{"shipping_method":null,"shipping_upgrade":{{shippingJson}}}]}""",
        };
        var item = new OrderItem { EtsyTransactionId = receipt * 10, Title = "Sample item", Quantity = 1 };
        item.Variations.Add(new OrderVariation { PropertyId = 200, Name = "Color", Value = "Blue" });
        if (personalization is not null)
            item.Variations.Add(new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = personalization });
        order.Items.Add(item);
        return order;
    }

    private static List<Order> Orders() =>
    [
        Sample(1, createdDay: 10, shipByDay: 22, country: "US", shipping: "Standard"),
        Sample(2, createdDay: 12, shipByDay: 19, country: "ca", shipping: "Express", note: "Please hurry"),
        Sample(3, createdDay: 14, shipByDay: null, country: "US", gift: true, personalization: "Bob"),
        Sample(4, createdDay: 11, shipByDay: 20, country: "DE", shipping: "Standard", personalization: " "),
    ];

    private static long[] Receipts(IEnumerable<Order> orders) => [.. orders.Select(o => o.EtsyReceiptId)];

    [Fact]
    public void Ship_by_date_is_the_default_soonest_first_with_undated_orders_last()
    {
        Assert.Equal([2L, 4, 1, 3], Receipts(new OpenOrderFilter().Apply(Orders())));
    }

    [Fact]
    public void Newest_and_oldest_sort_by_when_the_order_was_placed()
    {
        Assert.Equal([3L, 2, 4, 1], Receipts(new OpenOrderFilter { Sort = OpenOrderFilter.Sorts.Newest }.Apply(Orders())));
        Assert.Equal([1L, 4, 2, 3], Receipts(new OpenOrderFilter { Sort = OpenOrderFilter.Sorts.Oldest }.Apply(Orders())));
    }

    [Fact]
    public void Toggles_keep_orders_with_a_buyer_note_a_gift_or_personalization()
    {
        Assert.Equal([2L], Receipts(new OpenOrderFilter { HasNote = true }.Apply(Orders())));
        Assert.Equal([3L], Receipts(new OpenOrderFilter { Gift = true }.Apply(Orders())));
        // A blank personalization answer doesn't count.
        Assert.Equal([3L], Receipts(new OpenOrderFilter { Personalized = true }.Apply(Orders())));
        Assert.Empty(new OpenOrderFilter { Gift = true, HasNote = true }.Apply(Orders()));
    }

    [Fact]
    public void Shipping_and_destination_come_from_the_stored_receipt()
    {
        Assert.Equal([4L, 1], Receipts(new OpenOrderFilter { Shipping = "Standard" }.Apply(Orders())));
        Assert.Equal([1L, 3], Receipts(new OpenOrderFilter { Destination = "US" }.Apply(Orders())));
        Assert.Equal([2L], Receipts(new OpenOrderFilter { Destination = "CA" }.Apply(Orders())));
        Assert.Equal("CA", OrderPrintService.CountryIso(Orders()[1]));
        Assert.Null(OrderPrintService.ShippingMethod(Orders()[2]));
        Assert.True(new OpenOrderFilter { Destination = "US" }.IsFiltered);
        Assert.False(new OpenOrderFilter { Sort = OpenOrderFilter.Sorts.Newest }.IsFiltered);
    }

    [Fact]
    public void An_order_with_an_unreadable_receipt_only_drops_out_of_the_receipt_filters()
    {
        var broken = Sample(5, createdDay: 9, shipByDay: 18);
        broken.RawJson = "not json";
        Assert.Equal(5L, new OpenOrderFilter().Apply([broken]).Single().EtsyReceiptId);
        Assert.Empty(new OpenOrderFilter { Destination = "US" }.Apply([broken]));
    }
}
