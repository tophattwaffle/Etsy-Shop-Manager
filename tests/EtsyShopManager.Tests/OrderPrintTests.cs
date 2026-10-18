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
            EtsyTransactionId = tx, EtsyListingId = listingId, Title = title, Quantity = qty, PriceAmount = 1299,
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
            RawJson = """{"name":"Sample Buyer","formatted_address":"Sample Buyer\n1 Example Street\nApt 2\nSpringfield, IL 62701\nUnited States"}""",
            Items = [Item(1, 10, "Mini Caution Sign | desk decor | gift", 2, "Yellow", "Bob"), Item(2, 11, "Desk Cone", 1, null)],
        },
        new()
        {
            Id = 2, ShopId = 1, EtsyReceiptId = 1002, BuyerName = "Other Sample", IsPaid = true, Status = "paid",
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
        Assert.Equal(["9 Test Road", "Testville, CA 90001", "US"], parts.Lines);

        var broken = OrderPrintService.Address(new Order { BuyerName = "Fallback", RawJson = "not json" });
        Assert.Equal(("Fallback", 0), (broken.Name, broken.Lines.Count));
        Assert.Equal([3, 1], OrderPrintService.ParseIds("3, x,1,3,-2"));
    }

    [Fact]
    public async Task Thank_you_lines_are_kept_per_shop()
    {
        using var db = new TestDb();
        var settings = new InMemorySettings();
        var print = new OrderPrintService(db, settings, new NullAuditLog());
        Assert.Null(await print.SetThanksAsync(1, "  Thanks for shopping small!  ", "tophattwaffle"));
        Assert.Null(await print.SetThanksAsync(2, "", "tophattwaffle"));
        Assert.Equal("Keep the thank-you line to 300 characters.", await print.SetThanksAsync(1, new string('x', 301), "tophattwaffle"));
        Assert.Equal(new Dictionary<int, string> { [1] = "Thanks for shopping small!" }, await print.GetThanksAsync([1, 2]));
    }

    [Fact]
    public async Task Printouts_render_the_selected_orders_only()
    {
        using var db = new TestDb();
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Shops.Add(new Shop { Id = 1, EtsyShopId = 42, Name = "SampleShop", ConnectedAtUtc = DateTime.UtcNow });
            foreach (var o in SampleOrders()) ctx.Orders.Add(o);
            await ctx.SaveChangesAsync();
        }
        var settings = new InMemorySettings();
        await new OrderPrintService(db, settings, new NullAuditLog()).SetThanksAsync(1, "Thanks for shopping small!", null);

        var slips = await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=2,1");
        Assert.Equal(2, CountOf(slips, "<section class=\"slip\""));
        Assert.True(slips.IndexOf("Order #1002", StringComparison.Ordinal) < slips.IndexOf("Order #1001", StringComparison.Ordinal)); // ship-by first
        foreach (var text in new[] { "1 Example Street", "Testville, CA 90001", "Personalization: Bob", "Color: Yellow", "Please hurry",
                     "Happy birthday!", "Thanks for shopping small!", "SampleShop" })
            Assert.Contains(text, slips);

        var pick = await RenderAsync<PrintPickList>(db, settings, "orders/print/pick?ids=1");
        Assert.Contains("Mini Caution Sign", pick);
        Assert.Contains("×2", pick);
        Assert.DoesNotContain("1002", pick);

        Assert.Contains("No orders selected", await RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips"));
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
