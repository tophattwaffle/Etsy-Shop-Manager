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
    public void Has_note_filter_reflects_local_overrides()
    {
        var orders = Orders();
        // Receipt 2 originally had a note; clear it via override
        orders.Single(o => o.EtsyReceiptId == 2).MessageFromBuyerOverride = "";
        Assert.Empty(new OpenOrderFilter { HasNote = true }.Apply(orders));

        // Receipt 1 originally had no note; add one via override
        orders.Single(o => o.EtsyReceiptId == 1).MessageFromBuyerOverride = "Added buyer note";
        Assert.Equal([1L], Receipts(new OpenOrderFilter { HasNote = true }.Apply(orders)));
    }


    [Fact]
    public void Shipping_method_and_destination_come_from_the_stored_receipt()
    {
        Assert.Equal([4L, 1], Receipts(new OpenOrderFilter { Shipping = "Standard" }.Apply(Orders())));
        Assert.Equal("CA", OrderPrintService.CountryIso(Orders()[1]));
        Assert.Null(OrderPrintService.ShippingMethod(Orders()[2]));
        Assert.True(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.Domestic }.IsFiltered);
        Assert.False(new OpenOrderFilter { Sort = OpenOrderFilter.Sorts.Newest }.IsFiltered);
    }

    [Fact]
    public void Destination_is_the_shops_own_country_or_international_like_Etsys_app()
    {
        // No origin country on file: the shop counts as US-based.
        Assert.Equal([1L, 3], Receipts(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.Domestic }.Apply(Orders())));
        Assert.Equal([2L, 4], Receipts(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.International }.Apply(Orders())));

        var canadian = new Dictionary<int, string> { [1] = "CA" };
        Assert.Equal([2L], Receipts(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.Domestic, HomeCountries = canadian }.Apply(Orders())));
    }

    [Fact]
    public void An_order_with_an_unreadable_receipt_only_drops_out_of_the_receipt_filters()
    {
        var broken = Sample(5, createdDay: 9, shipByDay: 18);
        broken.RawJson = "not json";
        Assert.Equal(5L, new OpenOrderFilter().Apply([broken]).Single().EtsyReceiptId);
        Assert.Empty(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.Domestic }.Apply([broken]));
        Assert.Empty(new OpenOrderFilter { Destination = OpenOrderFilter.Destinations.International }.Apply([broken]));
    }

    [Fact]
    public async Task A_shops_home_country_is_where_most_of_its_shipping_profiles_ship_from()
    {
        using var db = new TestDb();
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Shops.AddRange(new Shop { Id = 1, EtsyShopId = 11, Name = "ShopOne" }, new Shop { Id = 2, EtsyShopId = 22, Name = "ShopTwo" },
                new Shop { Id = 3, EtsyShopId = 33, Name = "ShopThree" });
            ctx.ShippingProfiles.AddRange(
                new ShippingProfile { ShopId = 1, EtsyShippingProfileId = 1, OriginCountryIso = "us" },
                new ShippingProfile { ShopId = 1, EtsyShippingProfileId = 2, OriginCountryIso = "US" },
                new ShippingProfile { ShopId = 1, EtsyShippingProfileId = 3, OriginCountryIso = "CA" },
                new ShippingProfile { ShopId = 2, EtsyShippingProfileId = 4, OriginCountryIso = "GB" },
                new ShippingProfile { ShopId = 3, EtsyShippingProfileId = 5, OriginCountryIso = null });
            await ctx.SaveChangesAsync();
        }
        var homes = await new OrderQueries(db, TimeProvider.System).GetHomeCountriesAsync();
        Assert.Equal(new Dictionary<int, string> { [1] = "US", [2] = "GB" }, homes);
    }

    [Fact]
    public void Made_filter_splits_orders_with_every_item_made_from_those_with_something_to_make()
    {
        var orders = Orders();
        orders[0].Items[0].MadeQuantity = 1;                       // 1: its one item made
        orders[1].Items.Add(new OrderItem { EtsyTransactionId = 21, Quantity = 3, MadeQuantity = 2 }); // 2: one of three left
        orders[1].Items[0].MadeQuantity = 1;
        orders[2].Items[0].Quantity = 0;                           // 3: Etsy says 0, still one to make; marked made
        orders[2].Items[0].MadeQuantity = 1;
        // 4: nothing made

        var allMade = new OpenOrderFilter { Made = OpenOrderFilter.MadeStates.AllMade };
        var toMake = new OpenOrderFilter { Made = OpenOrderFilter.MadeStates.ToMake };
        Assert.Equal([1L, 3], Receipts(allMade.Apply(orders)).Order());
        Assert.Equal([2L, 4], Receipts(toMake.Apply(orders)).Order());
        Assert.True(allMade.IsFiltered);
        Assert.Equal(4, new OpenOrderFilter().Apply(orders).Count);

        // Marking the last unit moves the order across.
        orders[1].Items[1].MadeQuantity = 3;
        Assert.Equal([1L, 2, 3], Receipts(allMade.Apply(orders)).Order());
    }
}
