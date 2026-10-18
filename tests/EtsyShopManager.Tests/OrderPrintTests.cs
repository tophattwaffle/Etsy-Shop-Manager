using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Components.Pages.Orders;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

/// <summary>Pick lists and packing slips, with made-up sample orders (no customer data).</summary>
public class OrderPrintTests
{
    private static OrderItem Item(long tx, long listingId, string title, int qty, string? color, string? personalization = null)
    {
        var item = new OrderItem
        {
            EtsyTransactionId = tx, EtsyListingId = listingId, Title = title, Quantity = qty, PriceAmount = 1299, CurrencyCode = "USD",
            ImageUrl = $"https://i.etsystatic.com/sample/{listingId}.jpg",
        };
        if (color is not null) item.Variations.Add(new OrderVariation { PropertyId = 200, Name = "Color", Value = color });
        if (personalization is not null)
            item.Variations.Add(new OrderVariation { PropertyId = HaPayloadBuilder.PersonalizationPropertyId, Name = "Personalization", Value = personalization });
        return item;
    }

    private static List<Order> SampleOrders() =>
    [
        new()
        {
            Id = 1, ShopId = 1, EtsyReceiptId = 1001, BuyerName = "Sample Buyer", IsPaid = true, Status = "paid",
            EtsyCreatedAtUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), ShipByUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            MessageFromBuyer = "Please hurry", IsGift = true, GiftMessage = "Happy birthday!",
            CurrencyCode = "USD",
            RawJson = """
                {"name":"Sample Buyer","formatted_address":"Sample Buyer\n1 Example Street\nApt 2\nSpringfield, IL 62701\nUnited States",
                 "payment_method":"cc", "is_gift":true, "gift_message":"Happy birthday!", "gift_sender":"Grandma Sample",
                 "total_price":{"amount":3897,"divisor":100,"currency_code":"USD"},
                 "discount_amt":{"amount":500,"divisor":100,"currency_code":"USD"},
                 "total_tax_cost":{"amount":140,"divisor":100,"currency_code":"USD"},
                 "total_vat_cost":{"amount":0,"divisor":100,"currency_code":"USD"},
                 "total_shipping_cost":{"amount":582,"divisor":100,"currency_code":"USD"},
                 "grandtotal":{"amount":4119,"divisor":100,"currency_code":"USD"},
                 "transactions":[{"shipping_method":null,"shipping_upgrade":null},{"shipping_method":"USPS First-Class Mail","shipping_upgrade":null}]}
                """,
            Items = [Item(1, 10, "Mini Caution Sign | desk decor | gift", 2, "Yellow", "Bob"), Item(2, 11, "Desk Cone", 1, null)],
        },
        new()
        {
            Id = 2, ShopId = 1, EtsyReceiptId = 1002, BuyerName = "Other Sample", IsPaid = true, Status = "paid", CurrencyCode = "USD",
            EtsyCreatedAtUtc = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), ShipByUtc = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc),
            RawJson = """{"name":"Other Sample","first_line":"9 Test Road","city":"Testville","state":"CA","zip":"90001","country_iso":"US"}""",
            Items = [Item(3, 10, "Mini Caution Sign | desk decor", 3, "Yellow", "Alice"), Item(4, 10, "Mini Caution Sign", 1, "Blue")],
        },
    ];

    [Fact]
    public void Pick_list_groups_items_by_listing_and_variation_with_personalizations_under_them()
    {
        var lines = OrderPrintService.PickList(SampleOrders());

        Assert.Equal(3, lines.Count);
        var yellow = lines[0]; // most needed first
        Assert.Equal(("Mini Caution Sign", 5), (yellow.Title, yellow.Quantity));
        Assert.Equal(["Color: Yellow"], yellow.Variations);
        Assert.Equal([1001L, 1002L], yellow.Receipts);
        Assert.Equal([(1001L, 2, "Bob"), (1002L, 3, "Alice")], yellow.Personalizations);
        Assert.Contains(lines, l => l.Title == "Mini Caution Sign" && l.Variations.SequenceEqual(["Color: Blue"]) && l.Quantity == 1);
        Assert.Contains(lines, l => l.Title == "Desk Cone" && l.Variations.Count == 0 && l.Personalizations.Count == 0);
    }

    [Fact]
    public void Addresses_come_from_the_receipt_formatted_or_from_its_parts()
    {
        var orders = SampleOrders();
        var formatted = OrderPrintService.Address(orders[0]);
        Assert.Equal("Sample Buyer", formatted.Name);
        Assert.Equal(["1 Example Street", "Apt 2", "Springfield, IL 62701", "United States"], formatted.Lines);

        var parts = OrderPrintService.Address(orders[1]);
        Assert.Equal(["9 Test Road", "Testville, CA 90001", "United States"], parts.Lines);

        var broken = OrderPrintService.Address(new Order { BuyerName = "Fallback", RawJson = "not json" });
        Assert.Equal(("Fallback", 0), (broken.Name, broken.Lines.Count));
        Assert.Equal([3, 1], OrderPrintService.ParseIds("3, x,1,3,-2"));
    }

    [Fact]
    public void Slip_details_follow_Etsys_slip_and_leave_out_what_the_receipt_lacks()
    {
        var orders = SampleOrders();
        var full = OrderPrintService.Details(orders[0]);
        Assert.Equal(("Paid via Etsy Payments", "USPS First-Class Mail"), (full.PaymentMethod, full.ShippingMethod));
        Assert.Equal([("Item total", "$38.97"), ("Discount", "-$5.00"), ("Tax", "$1.40"), ("Shipping total", "$5.82")], full.Totals);
        Assert.Equal("$41.19", full.OrderTotal);

        var bare = OrderPrintService.Details(orders[1]); // no payment, shipping method or totals in this receipt
        Assert.Equal((null, null), (bare.PaymentMethod, bare.ShippingMethod));
        Assert.Equal([("Item total", "$51.96")], bare.Totals); // from the items: 3 × 12.99 + 1 × 12.99
        Assert.Equal("$51.96", bare.OrderTotal);

        Assert.Equal("2 x $12.99", OrderPrintService.LinePrice(orders[0].Items[0]));
        Assert.Equal("sampleshop.etsy.com", OrderPrintService.ShopAddress(new Shop { Name = "SampleShop" }));
    }

    [Fact]
    public async Task Packing_slip_notes_are_kept_per_shop()
    {
        using var db = new TestDb();
        var settings = new InMemorySettings();
        var print = new OrderPrintService(db, settings, new NullAuditLog());
        Assert.Null(await print.SetNoteAsync(1, "  Thanks for shopping small!  ", "tophattwaffle"));
        Assert.Null(await print.SetNoteAsync(2, "", "tophattwaffle"));
        Assert.Equal("Keep the packing slip note to 500 characters.", await print.SetNoteAsync(1, new string('x', 501), "tophattwaffle"));
        Assert.Equal(new Dictionary<int, string> { [1] = "Thanks for shopping small!" }, await print.GetNotesAsync([1, 2]));
    }

    [Fact]
    public async Task Slip_location_lines_are_kept_per_shop_and_blank_means_none()
    {
        using var db = new TestDb();
        var settings = new InMemorySettings();
        var print = new OrderPrintService(db, settings, new NullAuditLog());
        Assert.Null(await print.SetLocationAsync(1, "  Testville,   California ", "tophattwaffle"));
        Assert.Null(await print.SetLocationAsync(2, "Somewhere", "tophattwaffle"));
        Assert.Null(await print.SetLocationAsync(2, "   ", "tophattwaffle"));
        Assert.Equal("Keep the location line to 100 characters.", await print.SetLocationAsync(1, new string('x', 101), "tophattwaffle"));
        Assert.Equal(new Dictionary<int, string> { [1] = "Testville, California" }, await print.GetLocationsAsync([1, 2]));
        Assert.Empty(await print.GetNotesAsync([1, 2])); // kept apart from the packing slip note
    }

    [Fact]
    public async Task Completed_and_shipped_orders_still_print_from_history()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: true, closed: true);
        var settings = new InMemorySettings();

        var slips = await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=1,2");
        Assert.Equal((1, 1), (CountOf(slips, "<section class=\"gift-slip\""), CountOf(slips, "<section class=\"slip\"")));
        Assert.Contains("Happy birthday!", slips);
        Assert.Contains("Mini Caution Sign", await RenderAsync<PrintPickList>(db, settings, "orders/print/pick?ids=1"));
    }

    private static async Task SeedAsync(TestDb db, bool firstIsGift, bool closed = false)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Shops.Add(new Shop
        {
            Id = 1, EtsyShopId = 42, Name = "SampleShop", Title = "Little things, made well", TransactionSoldCount = 4321,
            ConnectedAtUtc = DateTime.UtcNow,
        });
        var orders = SampleOrders();
        orders[0].IsGift = firstIsGift;
        if (closed)
        {
            (orders[0].Status, orders[0].IsShipped) = ("completed", true);
            orders[1].Status = "canceled";
            Assert.DoesNotContain(orders, o => o.IsOpen);
        }
        foreach (var o in orders) ctx.Orders.Add(o);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Printouts_render_the_selected_orders_only()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: false);
        var settings = new InMemorySettings();
        await new OrderPrintService(db, settings, new NullAuditLog()).SetNoteAsync(1, "Thanks for shopping small!", null);

        var slips = await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=2,1");
        Assert.Equal(2, CountOf(slips, "<section class=\"slip\""));
        Assert.True(slips.IndexOf("#1002", StringComparison.Ordinal) < slips.IndexOf("#1001", StringComparison.Ordinal)); // ship-by first
        foreach (var text in new[] { "1 Example Street", "Testville, CA 90001", "Personalization: Bob", "Color: Yellow", "Please hurry",
                     "A note from the shop", "Thanks for shopping small!", "SampleShop", "sampleshop.etsy.com", "Paid via Etsy Payments",
                     "USPS First-Class Mail", "Order total", "$41.19", "2 x $12.99", "3 items", "4 items", "Scheduled to ship by" })
            Assert.Contains(text, slips);
        Assert.True(slips.IndexOf("A note from the shop", StringComparison.Ordinal) < slips.IndexOf("4 items", StringComparison.Ordinal));

        var pick = await RenderAsync<PrintPickList>(db, settings, "orders/print/pick?ids=1,2");
        pick = await RenderAsync<PrintPickList>(db, settings, "orders/print/pick?ids=1");
        Assert.Contains("Mini Caution Sign", pick);
        Assert.Contains("×2", pick);
        Assert.DoesNotContain("1002", pick);

        Assert.Contains("No orders selected", await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips"));
    }

    [Fact]
    public async Task Gift_orders_print_a_gift_slip_without_prices()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: true);
        var settings = new InMemorySettings();
        await new OrderPrintService(db, settings, new NullAuditLog()).SetLocationAsync(1, "Testville, California", null);

        var slips = await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=1,2");
        Assert.Equal((1, 1), (CountOf(slips, "<section class=\"gift-slip\""), CountOf(slips, "<section class=\"slip\"")));
        Assert.Equal(2, CountOf(slips, "Testville, California")); // the location line, on the gift slip and the normal one
        var gift = slips[slips.IndexOf("<section class=\"gift-slip\"", StringComparison.Ordinal)..];
        gift = gift[..(gift.IndexOf("</section>", StringComparison.Ordinal) + 10)];
        foreach (var text in new[] { "SampleShop", "Little things, made well", "4321 Sales", "Order #1001", "Shipped to: Sample Buyer",
                     "Mini Caution Sign | desk decor | gift", "Color: Yellow", "Personalization: Bob", "Quantity: 2", "Quantity: 1",
                     "A gift for you", "Happy birthday!", "- Grandma Sample", "Scan below to shop more from this independent seller.", "<svg" })
            Assert.Contains(text, gift);
        Assert.DoesNotContain("$", gift); // no prices on a gift slip
        Assert.DoesNotContain("1 Example Street", gift); // the address goes on the shipping label
        // The gift side opens with "A gift for you", not the shop's name (which read as if the gift were for the shop).
        var giftSide = gift[gift.IndexOf("class=\"gift-right\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SampleShop", giftSide);
        Assert.True(giftSide.IndexOf("Happy birthday!", StringComparison.Ordinal) < giftSide.IndexOf("- Grandma Sample", StringComparison.Ordinal));
        Assert.Equal(5, CountOf(giftSide, "class=\"gift-sparkle "));

        var qr = OrderPrintService.ShopQrSvg(new Shop { Name = "SampleShop" });
        Assert.StartsWith("<svg", qr);
        Assert.Contains("viewBox", qr);
    }

    [Fact]
    public async Task History_can_show_gift_orders_only()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: true);
        var queries = new OrderQueries(db, TimeProvider.System);
        Assert.Equal(2, (await queries.GetHistoryAsync(null, null, 0, 50)).Total);
        var (gifts, total) = await queries.GetHistoryAsync(null, null, 0, 50, giftsOnly: true);
        Assert.Equal((1, 1001L), (total, Assert.Single(gifts).EtsyReceiptId));
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    private static async Task<string> RenderAsync<T>(TestDb db, InMemorySettings settings, string relativeUrl) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<EtsyShopManager.Data.AppDbContext>>(db);
        services.AddSingleton<EtsyShopManager.Core.Services.ISettingsStore>(settings);
        services.AddSingleton<EtsyShopManager.Core.Services.IAuditLog, NullAuditLog>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OrderPrintService>();
        services.AddSingleton<ShopAccentService>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/" + relativeUrl));
        services.AddSupplyValueFromQueryProvider();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>()).ToHtmlString());
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
    }
}
