using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Components.Pages.Orders;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtsyShopManager.Tests;

/// <summary>Changes a buyer asked for after checkout, kept on the local order only (made-up sample data).</summary>
public class OrderOverrideTests
{
    private const long Personalization = HaPayloadBuilder.PersonalizationPropertyId;

    private static OrderItem Item(string? color = "Yellow", string? personalization = "Bob") => new()
    {
        Id = 10, EtsyTransactionId = 1, EtsyListingId = 500, Title = "Mini Caution Sign | desk decor", Quantity = 1, PriceAmount = 1299,
        Variations =
        [
            .. color is null ? Array.Empty<OrderVariation>() : [new OrderVariation { PropertyId = 513, ValueId = 1, Name = "Color", Value = color }],
            .. personalization is null ? Array.Empty<OrderVariation>() : [new OrderVariation { PropertyId = Personalization, Name = "Personalization", Value = personalization }],
            new OrderVariation { PropertyId = 514, ValueId = 7, Name = "Size", Value = "Small" },
        ],
    };

    private static Order SampleOrder(OrderItem item) => new()
    {
        Id = 1, ShopId = 1, EtsyReceiptId = 1001, BuyerName = "Sample Buyer", IsPaid = true, Status = "paid", CurrencyCode = "USD",
        EtsyCreatedAtUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), ShipByUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
        RawJson = """{"name":"Sample Buyer","formatted_address":"Sample Buyer\n1 Example Street\nSpringfield, IL 62701\nUnited States"}""",
        Items = [item],
    };

    [Fact]
    public void Overrides_replace_the_buyers_values_in_place()
    {
        var item = Item();
        Assert.Same(item.Variations, OrderOverrides.Variations(item)); // nothing changed: the buyer's as they are

        item.PersonalizationOverride = "Robert";
        item.VariationOverrides = [new OrderVariation { PropertyId = 513, ValueId = 2, Name = "Color", Value = "Blue" }];
        Assert.Equal(["Color: Blue", "Personalization: Robert", "Size: Small"], OrderOverrides.Variations(item).Select(v => $"{v.Name}: {v.Value}"));
        Assert.Equal("Bob", OrderOverrides.BuyersPersonalization(item));
        Assert.Equal("Yellow", item.Variations[0].Value); // Etsy's copy is untouched

        item.PersonalizationOverride = ""; // the buyer asked for none
        Assert.DoesNotContain(OrderOverrides.Variations(item), v => v.PropertyId == Personalization);

        var unpersonalized = Item(personalization: null);
        unpersonalized.PersonalizationOverride = "Added later";
        Assert.Equal("Personalization: Added later", OrderOverrides.Variations(unpersonalized).Select(v => $"{v.Name}: {v.Value}").Last());
    }

    [Fact]
    public void Home_assistant_to_make_pick_list_and_filters_show_the_changes()
    {
        var item = Item();
        item.PersonalizationOverride = "Robert";
        item.VariationOverrides = [new OrderVariation { PropertyId = 513, ValueId = 2, Name = "Color", Value = "Blue" }];
        var order = SampleOrder(item);

        var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);
        var detail = payload["open_orders"]![0]!["items"]![0]!;
        Assert.Equal("Robert", (string?)detail["personalization"]);
        Assert.Equal("Color: Blue, Size: Small", (string?)detail["variations_text"]);
        Assert.Equal("Blue", (string?)detail["variations"]![0]!["value"]); // the card colors this pill blue
        var needed = payload["items_needed"]![0]!;
        Assert.Equal(("Robert", "Color: Blue, Size: Small"), ((string?)needed["personalization"], (string?)needed["variations_text"]));

        var (variations, personalization) = OrderPrintService.Split(item);
        Assert.Equal(["Color: Blue", "Size: Small"], variations);
        Assert.Equal("Robert", personalization);
        Assert.Equal("Robert", Assert.Single(Assert.Single(OrderPrintService.PickList([order])).Personalizations).Text);

        item.PersonalizationOverride = "";
        Assert.False(OpenOrderFilter.IsPersonalized(order));
    }

    [Fact]
    public void An_address_override_is_where_the_order_ships()
    {
        var order = SampleOrder(Item());
        Assert.Equal("1 Example Street", OrderPrintService.Address(order).Lines[0]);

        order.AddressOverride = "Sample Buyer\n22 New Road\nApt 3\nShelbyville, IL 62565\nUnited States";
        var address = OrderPrintService.Address(order);
        Assert.Equal("Sample Buyer", address.Name);
        Assert.Equal(["22 New Road", "Apt 3", "Shelbyville, IL 62565", "United States"], address.Lines);
        Assert.Equal("1 Example Street", OrderPrintService.EtsyAddress(order).Lines[0]);
        Assert.True(OrderOverrides.HasOverride(order));
    }

    [Fact]
    public void Polling_the_order_again_keeps_the_changes()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "receipts.json")))!.AsArray()
            .First(n => n!["transactions"]!.AsArray().Count > 0)!;
        var receipt = node.Deserialize<EtsyReceipt>(EtsyJson.Options)!;
        var order = new Order();
        OrderMapper.Apply(order, receipt, node.ToJsonString(), DateTime.UtcNow);

        var item = order.Items[0];
        order.AddressOverride = "Sample Buyer\n22 New Road";
        item.PersonalizationOverride = "Robert";
        item.VariationOverrides = [new OrderVariation { PropertyId = 513, Name = "Color", Value = "Blue" }];
        OrderMapper.Apply(order, receipt, node.ToJsonString(), DateTime.UtcNow);

        Assert.Same(item, order.Items[0]);
        Assert.Equal(("Sample Buyer\n22 New Road", "Robert", "Blue"),
            (order.AddressOverride, item.PersonalizationOverride, Assert.Single(item.VariationOverrides).Value));
    }

    [Fact]
    public void Variation_choices_come_from_the_listing()
    {
        PropertyValue P(long id, string name, long valueId, string value) =>
            new() { PropertyId = id, PropertyName = name, ValueIds = [valueId], Values = [value] };
        var listing = new[] { P(513, "Color", 1, "Yellow"), P(514, "Size", 7, "Small"), P(513, "Color", 2, "Blue"), P(514, "Size", 8, "Large"), P(513, "Color", 1, "Yellow") };

        var buyers = new OrderVariation { PropertyId = 513, ValueId = 1, Name = "Color", Value = "Yellow" };
        Assert.Equal(["Yellow", "Blue"], OrderOverrideService.Options(listing, buyers).Select(o => o.Value));
        // A value the listing no longer offers is still listed, so the dropdown can show what the buyer chose.
        Assert.Equal(["Green", "Yellow", "Blue"], OrderOverrideService.Options(listing, buyers with { Value = "Green" }).Select(o => o.Value));
        Assert.Empty(OrderOverrideService.Options([], buyers));
    }

    [Fact]
    public async Task A_seller_note_is_saved_shown_again_and_cleared()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item());
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        Assert.Equal("", form.SellerNote);
        form.SellerNote = new string('x', OrderOverrides.MaxSellerNoteLength + 1);
        Assert.Equal($"Keep the seller note to {OrderOverrides.MaxSellerNoteLength} characters.", await service.SaveAsync(form, "tophattwaffle"));

        form.SellerNote = "  Buyer called: ship Monday \n";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.Include(o => o.Items).SingleAsync();
            Assert.Equal("Buyer called: ship Monday", order.SellerNote);
            Assert.False(OrderOverrides.HasOverride(order)); // a note isn't a change to what ships
        }
        Assert.Equal("seller note set", Assert.Single(audit.Entries).Details);

        form = (await service.GetFormAsync(1))!;
        Assert.Equal("Buyer called: ship Monday", form.SellerNote);
        form.SellerNote = "   ";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
            Assert.Null((await ctx.Orders.SingleAsync()).SellerNote);
        Assert.Equal("seller note removed", audit.Entries[^1].Details);
    }

    [Fact]
    public async Task A_seller_note_prints_on_slips_only_when_included()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item());
        var service = new OrderOverrideService(db, new RecordingAudit());

        var form = (await service.GetFormAsync(1))!;
        form.SellerNote = "Made the large one in blue,\nas you asked.";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        Assert.DoesNotContain("Note from SampleShop", await RenderSlipsAsync(db)); // shown on the order card only

        form = (await service.GetFormAsync(1))!;
        Assert.False(form.SellerNoteOnSlips);
        form.SellerNoteOnSlips = true;
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        var slip = await RenderSlipsAsync(db);
        Assert.Contains("Note from SampleShop", slip);
        Assert.Contains("Made the large one in blue,", slip); // the line break is kept (encoded; slip-pre shows it)
        Assert.Contains("as you asked.", slip);

        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsGift, true));
        Assert.Contains("class=\"gift-seller-note\"", await RenderSlipsAsync(db));

        // Clearing the note turns printing off too, so a later note doesn't print unasked.
        form = (await service.GetFormAsync(1))!;
        form.SellerNote = "";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
            Assert.False((await ctx.Orders.SingleAsync()).SellerNoteOnSlips);
        Assert.DoesNotContain("Note from SampleShop", await RenderSlipsAsync(db));
    }

    [Fact]
    public async Task Saving_keeps_only_real_changes_and_checks_them()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item());
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        Assert.Equal("Sample Buyer\n1 Example Street\nSpringfield, IL 62701\nUnited States", form.EtsyAddress);
        Assert.Equal(form.EtsyAddress, form.Address);
        var item = Assert.Single(form.Items);
        Assert.Equal(("Bob", "Bob", false), (item.BuyersPersonalization, item.Personalization, item.ChangePersonalization));
        var color = item.Variations.Single(v => v.Name == "Color");
        Assert.Equal(["Yellow", "Blue"], color.Options.Select(o => o.Value));

        // Switched on but the same as the buyer's: nothing to save.
        item.ChangePersonalization = true;
        form.ChangeAddress = true;
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        Assert.Empty(audit.Entries);

        color.Value = "Purple";
        Assert.Equal("Choose Color for Mini Caution Sign from the listing's values.", await service.SaveAsync(form, "tophattwaffle"));
        color.Value = "Blue";
        form.Address = "Sample Buyer";
        Assert.Equal("Enter the name and the address, each on its own line.", await service.SaveAsync(form, "tophattwaffle"));

        form.Address = "  Sample Buyer \r\n\r\n 22 New Road\nShelbyville, IL 62565  ";
        item.Personalization = " Robert ";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.Include(o => o.Items).SingleAsync();
            Assert.Equal("Sample Buyer\n22 New Road\nShelbyville, IL 62565", order.AddressOverride);
            var saved = Assert.Single(order.Items);
            Assert.Equal("Robert", saved.PersonalizationOverride);
            Assert.Equal((513L, 2L, "Blue"), (Assert.Single(saved.VariationOverrides).PropertyId, saved.VariationOverrides[0].ValueId, saved.VariationOverrides[0].Value));
            Assert.Equal("Yellow", saved.Variations[0].Value);
        }
        Assert.Equal(("order.changes", "1001", "tophattwaffle"), (audit.Entries[0].Action, audit.Entries[0].EntityId, audit.Entries[0].User));

        // Reopened, the form shows the changes; switching them off goes back to the buyer's.
        form = (await service.GetFormAsync(1))!;
        Assert.Equal((true, "Robert", "Blue"), (form.Items[0].ChangePersonalization, form.Items[0].Personalization, form.Items[0].Variations[0].Value));
        form.ChangeAddress = false;
        form.Items[0].ChangePersonalization = false;
        form.Items[0].Variations[0].Value = "Yellow";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.Include(o => o.Items).SingleAsync();
            Assert.False(OrderOverrides.HasOverride(order));
        }
    }

    [Fact]
    public async Task Packing_slips_mark_a_changed_address_and_print_the_changes()
    {
        using var db = new TestDb();
        var item = Item();
        item.PersonalizationOverride = "Robert";
        item.VariationOverrides = [new OrderVariation { PropertyId = 513, ValueId = 2, Name = "Color", Value = "Blue" }];
        await SeedAsync(db, item, address: "Sample Buyer\n22 New Road\nShelbyville, IL 62565");

        var slips = await RenderSlipsAsync(db);
        var shipTo = slips[slips.IndexOf("class=\"slip-changed", StringComparison.Ordinal)..];
        shipTo = shipTo[..shipTo.IndexOf("Scheduled to ship by", StringComparison.Ordinal)];
        foreach (var text in new[] { "Address changed", "22 New Road", "Shelbyville, IL 62565", "Change the shipping label", "1 Example Street" })
            Assert.Contains(text, shipTo);
        Assert.True(shipTo.IndexOf("22 New Road", StringComparison.Ordinal) < shipTo.IndexOf("1 Example Street", StringComparison.Ordinal));
        Assert.Contains("Personalization: Robert", slips);
        Assert.Contains("Color: Blue", slips);
        Assert.DoesNotContain("Bob", slips);

        // A gift slip carries the mark in its label half, away from what the recipient keeps.
        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.IsGift, true));
        var gift = await RenderSlipsAsync(db);
        Assert.Contains("class=\"slip-changed gift-changed\"", gift);
        Assert.Contains("Shipped to: Sample Buyer", gift);

        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.AddressOverride, (string?)null));
        Assert.DoesNotContain("Address changed", await RenderSlipsAsync(db));
    }

    private static async Task SeedAsync(TestDb db, OrderItem item, string? address = null)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Shops.Add(new Shop { Id = 1, EtsyShopId = 42, Name = "SampleShop", ConnectedAtUtc = DateTime.UtcNow });
        var listing = new Listing { ShopId = 1, EtsyListingId = 500, Title = "Mini Caution Sign", State = "active" };
        foreach (var (color, colorId) in new[] { ("Yellow", 1L), ("Blue", 2L) })
        foreach (var (size, sizeId) in new[] { ("Small", 7L), ("Large", 8L) })
            listing.Products.Add(new ListingProduct
            {
                EtsyProductId = colorId * 10 + sizeId,
                PropertyValues =
                [
                    new PropertyValue { PropertyId = 513, PropertyName = "Color", ValueIds = [colorId], Values = [color] },
                    new PropertyValue { PropertyId = 514, PropertyName = "Size", ValueIds = [sizeId], Values = [size] },
                ],
            });
        ctx.Listings.Add(listing);
        var order = SampleOrder(item);
        order.AddressOverride = address;
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
    }

    private static async Task<string> RenderSlipsAsync(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<EtsyShopManager.Data.AppDbContext>>(db);
        services.AddSingleton<EtsyShopManager.Core.Services.ISettingsStore>(new InMemorySettings());
        services.AddSingleton<EtsyShopManager.Core.Services.IAuditLog, NullAuditLog>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OrderPrintService>();
        services.AddSingleton<SlipLayoutService>();
        services.AddSingleton<SlipDocumentService>();
        services.AddSingleton<ShopAccentService>();
        services.AddSingleton<NavigationManager>(new TestNavigation("http://localhost/orders/print/slips?ids=1"));
        services.AddSupplyValueFromQueryProvider();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PrintPackingSlips>()).ToHtmlString());
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
    }

    private sealed class RecordingAudit : EtsyShopManager.Core.Services.IAuditLog
    {
        public List<(string Action, string? EntityId, string? Details, string? User)> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityId, details, userName));
            return Task.CompletedTask;
        }
    }
}
