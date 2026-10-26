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
using MudBlazor.Services;

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

        var orderWithNote = SampleOrder(Item());
        orderWithNote.MessageFromBuyer = "Please wrap";
        Assert.Equal("Please wrap", OrderOverrides.Note(orderWithNote));
        orderWithNote.MessageFromBuyerOverride = "Please do not wrap";
        Assert.Equal("Please do not wrap", OrderOverrides.Note(orderWithNote));
        Assert.Equal("Please wrap", orderWithNote.MessageFromBuyer); // Etsy's copy is untouched
        Assert.True(OrderOverrides.HasOverride(orderWithNote));

        orderWithNote.MessageFromBuyerOverride = ""; // the buyer's note was cleared
        Assert.Null(OrderOverrides.Note(orderWithNote));
        Assert.Equal("Please wrap", orderWithNote.MessageFromBuyer);

        var orderWithoutNote = SampleOrder(Item());
        orderWithoutNote.MessageFromBuyerOverride = "Added later";
        Assert.Equal("Added later", OrderOverrides.Note(orderWithoutNote));
        Assert.Null(orderWithoutNote.MessageFromBuyer);
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

        order.MessageFromBuyer = "Original note";
        order.MessageFromBuyerOverride = "Modified note";
        payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);
        Assert.Equal("Modified note", (string?)payload["open_orders"]![0]!["message_from_buyer"]);
        Assert.True(OpenOrderFilter.HasBuyerNote(order));

        order.MessageFromBuyerOverride = ""; // cleared
        payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);
        Assert.False(payload["open_orders"]![0]!.AsObject().ContainsKey("message_from_buyer"));
        Assert.False(OpenOrderFilter.HasBuyerNote(order));

        var orderAdded = SampleOrder(Item());
        orderAdded.MessageFromBuyerOverride = "Added note";
        payload = HaPayloadBuilder.OpenOrders([orderAdded], TimeZoneInfo.Utc);
        Assert.Equal("Added note", (string?)payload["open_orders"]![0]!["message_from_buyer"]);
        Assert.True(OpenOrderFilter.HasBuyerNote(orderAdded));
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
        order.MessageFromBuyerOverride = "Preserved note";
        item.PersonalizationOverride = "Robert";
        item.VariationOverrides = [new OrderVariation { PropertyId = 513, Name = "Color", Value = "Blue" }];
        OrderMapper.Apply(order, receipt, node.ToJsonString(), DateTime.UtcNow);

        Assert.Same(item, order.Items[0]);
        Assert.Equal(("Sample Buyer\n22 New Road", "Preserved note", "Robert", "Blue"),
            (order.AddressOverride, order.MessageFromBuyerOverride, item.PersonalizationOverride, Assert.Single(item.VariationOverrides).Value));
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

    [Fact]
    public async Task Buyer_note_change_and_seller_note_save_side_by_side()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item(), note: "Original buyer note");
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        form.SellerNote = "Called the buyer";
        form.ChangeNote = true;
        form.Note = "Gift wrap please";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("Called the buyer", order.SellerNote);
            Assert.Equal("Gift wrap please", order.MessageFromBuyerOverride);
        }
        Assert.Contains("seller note set", audit.Entries[^1].Details);
        Assert.Contains("note changed", audit.Entries[^1].Details);

        // Putting the buyer's note back leaves the seller note alone.
        form = (await service.GetFormAsync(1))!;
        form.ChangeNote = false;
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("Called the buyer", order.SellerNote);
            Assert.Null(order.MessageFromBuyerOverride);
            Assert.Equal("Original buyer note", OrderOverrides.Note(order));
        }
        Assert.Equal("note back to Etsy's", audit.Entries[^1].Details);
    }

    [Fact]
    public async Task Order_note_override_service_validates_and_saves()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item(), note: "Original buyer note");
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        Assert.Equal("Original buyer note", form.BuyersNote);
        Assert.False(form.ChangeNote);
        Assert.Equal("Original buyer note", form.Note);

        // Same note as buyer's: nothing saved
        form.ChangeNote = true;
        form.Note = "Original buyer note";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        Assert.Empty(audit.Entries);

        // Validation: note > 1000 characters
        form.Note = new string('x', 1001);
        Assert.Equal("Keep the note to 1000 characters.", await service.SaveAsync(form, "tophattwaffle"));

        // Save modification
        form.Note = "  Please pack securely  \r\n";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("Please pack securely", order.MessageFromBuyerOverride);
            Assert.Equal("Original buyer note", order.MessageFromBuyer); // untouched
            Assert.Equal("Please pack securely", OrderOverrides.Note(order));
        }
        Assert.Equal(("order.changes", "1001", "note changed", "tophattwaffle"),
            (audit.Entries[^1].Action, audit.Entries[^1].EntityId, audit.Entries[^1].Details, audit.Entries[^1].User));

        // Form reopened shows existing override
        form = (await service.GetFormAsync(1))!;
        Assert.True(form.ChangeNote);
        Assert.Equal("Please pack securely", form.Note);
        Assert.Equal("Original buyer note", form.BuyersNote);

        // Clear note: Note = ""
        form.Note = "";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("", order.MessageFromBuyerOverride);
            Assert.Null(OrderOverrides.Note(order));
        }

        // Toggle change off returns to buyer's note
        form = (await service.GetFormAsync(1))!;
        Assert.True(form.ChangeNote);
        Assert.Equal("", form.Note);
        form.ChangeNote = false;
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Null(order.MessageFromBuyerOverride);
            Assert.Equal("Original buyer note", OrderOverrides.Note(order));
            Assert.False(OrderOverrides.HasOverride(order));
        }
        Assert.Equal(("order.changes", "1001", "note back to Etsy's", "tophattwaffle"),
            (audit.Entries[^1].Action, audit.Entries[^1].EntityId, audit.Entries[^1].Details, audit.Entries[^1].User));

        // Re-override and then restore by typing exact original note while ChangeNote is true
        form.ChangeNote = true;
        form.Note = "Different note";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        form = (await service.GetFormAsync(1))!;
        Assert.Equal("Different note", form.Note);
        form.Note = "Original buyer note"; // typing Etsy's original note
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Null(order.MessageFromBuyerOverride);
            Assert.Equal("Original buyer note", OrderOverrides.Note(order));
            Assert.False(OrderOverrides.HasOverride(order));
        }
        Assert.Equal(("order.changes", "1001", "note back to Etsy's", "tophattwaffle"),
            (audit.Entries[^1].Action, audit.Entries[^1].EntityId, audit.Entries[^1].Details, audit.Entries[^1].User));
    }

    [Fact]
    public async Task Adding_note_when_none_exists_and_removing_it()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item(), note: null);
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        Assert.Null(form.BuyersNote);
        Assert.False(form.ChangeNote);
        Assert.Equal("", form.Note);

        // Empty string with toggle on on order without note: nothing saved
        form.ChangeNote = true;
        form.Note = "   ";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        Assert.Empty(audit.Entries);

        // Add note
        form.Note = "Newly added order note";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("Newly added order note", order.MessageFromBuyerOverride);
            Assert.Null(order.MessageFromBuyer);
            Assert.Equal("Newly added order note", OrderOverrides.Note(order));
        }
        Assert.Equal(("order.changes", "1001", "note changed", "tophattwaffle"),
            (audit.Entries[^1].Action, audit.Entries[^1].EntityId, audit.Entries[^1].Details, audit.Entries[^1].User));

        // Re-open and clear added note by emptying the text
        form = (await service.GetFormAsync(1))!;
        Assert.True(form.ChangeNote);
        Assert.Equal("Newly added order note", form.Note);
        form.Note = "   "; // cleared
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Null(order.MessageFromBuyerOverride);
            Assert.Null(order.MessageFromBuyer);
            Assert.Null(OrderOverrides.Note(order));
            Assert.False(OrderOverrides.HasOverride(order));
        }
        Assert.Equal(("order.changes", "1001", "note back to Etsy's", "tophattwaffle"),
            (audit.Entries[^1].Action, audit.Entries[^1].EntityId, audit.Entries[^1].Details, audit.Entries[^1].User));

        // Add note again and clear by toggling switch off
        form = (await service.GetFormAsync(1))!;
        form.ChangeNote = true;
        form.Note = "Second note";
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        form = (await service.GetFormAsync(1))!;
        Assert.True(form.ChangeNote);
        form.ChangeNote = false;
        Assert.Null(await service.SaveAsync(form, "tophattwaffle"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Null(order.MessageFromBuyerOverride);
            Assert.Null(OrderOverrides.Note(order));
            Assert.False(OrderOverrides.HasOverride(order));
        }
    }

    [Fact]
    public async Task Packing_slips_reflect_note_overrides_including_special_characters()
    {
        using var db = new TestDb();
        var item = Item();
        await SeedAsync(db, item, note: "Buyer original slip note");

        var slips = await RenderSlipsAsync(db);
        Assert.Contains("Note from buyer", slips);
        Assert.Contains("Buyer original slip note", slips);

        // Modified note override
        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.MessageFromBuyerOverride, "Overridden slip note"));

        slips = await RenderSlipsAsync(db);
        Assert.Contains("Note from buyer", slips);
        Assert.Contains("Overridden slip note", slips);
        Assert.DoesNotContain("Buyer original slip note", slips);

        // Cleared note override
        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.MessageFromBuyerOverride, ""));

        slips = await RenderSlipsAsync(db);
        Assert.DoesNotContain("Buyer original slip note", slips);
        Assert.DoesNotContain("Overridden slip note", slips);
        Assert.DoesNotContain("Note from buyer", slips);

        // Special characters: HTML tags, ampersand, emoji
        var specialNote = "Fragile & <handle with care> 🚀📦";
        await using (var ctx = db.CreateDbContext())
            await ctx.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.MessageFromBuyerOverride, specialNote));

        slips = await RenderSlipsAsync(db);
        Assert.Contains("Fragile &amp; &lt;handle with care&gt;", slips);
        Assert.DoesNotContain("<handle with care>", slips);

        // In HA payload
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            var payload = HaPayloadBuilder.OpenOrders([order], TimeZoneInfo.Utc);
            Assert.Equal(specialNote, (string?)payload["open_orders"]![0]!["message_from_buyer"]);
        }
    }

    [Fact]
    public void Whitespace_only_notes_evaluate_to_null()
    {
        var order = SampleOrder(Item());
        order.MessageFromBuyer = "   ";
        Assert.Null(OrderOverrides.Note(order));

        order.MessageFromBuyerOverride = "  \t \n ";
        Assert.Null(OrderOverrides.Note(order));
    }

    [Fact]
    public async Task Changed_mark_renders_label_in_tooltip_and_aria_label()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html1 = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Label"] = "Note from buyer changed here. The buyer entered none.",
            });
            var output = await renderer.RenderComponentAsync<ChangedMark>(parameters);
            return output.ToHtmlString();
        });
        Assert.Contains("class=\"esm-changed-mark\"", html1);
        Assert.Contains("aria-label=\"Note from buyer changed here. The buyer entered none.\"", html1);
        Assert.Contains("Note from buyer changed here. The buyer entered none.", html1);

        var html2 = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Label"] = "Note from buyer changed here. The buyer entered: Original note",
            });
            var output = await renderer.RenderComponentAsync<ChangedMark>(parameters);
            return output.ToHtmlString();
        });
        Assert.Contains("aria-label=\"Note from buyer changed here. The buyer entered: Original note\"", html2);
        Assert.Contains("Note from buyer changed here. The buyer entered: Original note", html2);
    }

    [Fact]
    public async Task Order_card_renders_note_states_correctly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddMudServices();
        services.AddAuthorizationCore();
        var adminUser = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tophattwaffle"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, EtsyShopManager.Core.Modules.Roles.Admin)
        ], "auth"));
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(_ =>
            new TestAuthStateProvider(adminUser));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var ordersPage = new Orders();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        typeof(Orders).GetProperty("Time", flags)!.SetValue(ordersPage, TimeProvider.System);

        var orderCardMethod = typeof(Orders).GetMethod("OrderCard", flags)!;

        async Task<string> RenderOrderCardAsync(Order order)
        {
            var fragment = (RenderFragment)orderCardMethod.Invoke(ordersPage, [order, false])!;
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["ChildContent"] = fragment,
                });
                var output = await renderer.RenderComponentAsync<FragmentWrapper>(parameters);
                return output.ToHtmlString();
            });
        }

        var order = SampleOrder(Item());
        order.Shop = new Shop { Id = 1, Name = "TestShop" };

        // 1. Untouched note: buyer had note, no override
        order.MessageFromBuyer = "Original buyer note";
        order.MessageFromBuyerOverride = null;
        var htmlUntouched = await RenderOrderCardAsync(order);
        Assert.Contains("class=\"order-message\"", htmlUntouched);
        Assert.Contains("<b>Note from buyer:</b> Original buyer note", htmlUntouched);
        Assert.DoesNotContain("order-message esm-changed", htmlUntouched);
        Assert.DoesNotContain("No note from buyer", htmlUntouched);

        // 2. Modified note: buyer had note, overridden to new text
        order.MessageFromBuyer = "Original buyer note";
        order.MessageFromBuyerOverride = "Modified buyer note";
        var htmlModified = await RenderOrderCardAsync(order);
        Assert.Contains("order-message esm-changed", htmlModified);
        Assert.Contains("<b>Note from buyer:</b> Modified buyer note", htmlModified);
        Assert.Contains("Note from buyer changed here. The buyer entered: Original buyer note", htmlModified);

        // 3. Added note: buyer had no note, overridden to new text
        order.MessageFromBuyer = null;
        order.MessageFromBuyerOverride = "Newly added note";
        var htmlAdded = await RenderOrderCardAsync(order);
        Assert.Contains("order-message esm-changed", htmlAdded);
        Assert.Contains("<b>Note from buyer:</b> Newly added note", htmlAdded);
        Assert.Contains("Note from buyer changed here. The buyer entered none.", htmlAdded);

        // 4. Cleared note: buyer had note, overridden to empty string
        order.MessageFromBuyer = "Original buyer note";
        order.MessageFromBuyerOverride = "";
        var htmlCleared = await RenderOrderCardAsync(order);
        Assert.Contains("order-message esm-changed", htmlCleared);
        Assert.Contains("No note from buyer", htmlCleared);
        Assert.Contains("Note from buyer changed here. The buyer entered: Original buyer note", htmlCleared);

        // 5. No note at all: buyer had no note, no override
        order.MessageFromBuyer = null;
        order.MessageFromBuyerOverride = null;
        var htmlNone = await RenderOrderCardAsync(order);
        Assert.DoesNotContain("Note from buyer", htmlNone);
        Assert.DoesNotContain("No note from buyer", htmlNone);
    }

    [Fact]
    public async Task Order_note_override_at_boundary_lengths()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item(), note: "Buyer note");
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var form = (await service.GetFormAsync(1))!;
        form.ChangeNote = true;

        // Exactly 1000 characters should succeed
        form.Note = new string('A', 1000);
        Assert.Null(await service.SaveAsync(form, "admin"));
        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal(1000, order.MessageFromBuyerOverride!.Length);
            Assert.Equal(new string('A', 1000), OrderOverrides.Note(order));
        }

        // 1001 characters should fail validation
        form.Note = new string('A', 1001);
        Assert.Equal("Keep the note to 1000 characters.", await service.SaveAsync(form, "admin"));
    }

    [Fact]
    public async Task OrderChangesDialog_initializes_and_enforces_admin_role_on_save()
    {
        using var db = new TestDb();
        await SeedAsync(db, Item(), note: "Buyer original note");
        var audit = new RecordingAudit();
        var service = new OrderOverrideService(db, audit);

        var dialog = new OrderChangesDialog();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        typeof(OrderChangesDialog).GetProperty("Overrides", flags)!.SetValue(dialog, service);
        typeof(OrderChangesDialog).GetProperty("OrderId", flags)!.SetValue(dialog, 1);

        // Initialize dialog
        var initMethod = typeof(OrderChangesDialog).GetMethod("OnInitializedAsync", flags)!;
        await (Task)initMethod.Invoke(dialog, null)!;

        var formField = typeof(OrderChangesDialog).GetField("_form", flags)!;
        var form = (OrderChangesForm)formField.GetValue(dialog)!;
        Assert.NotNull(form);
        Assert.Equal("Buyer original note", form.BuyersNote);
        Assert.False(form.ChangeNote);
        Assert.Equal("Buyer original note", form.Note);

        // Non-admin attempting to save
        form.ChangeNote = true;
        form.Note = "Edited note by non-admin";
        var nonAdminUser = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "worker"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, EtsyShopManager.Core.Modules.Roles.User)
        ], "auth"));
        typeof(OrderChangesDialog).GetProperty("AuthState", flags)!.SetValue(dialog, Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(nonAdminUser)));

        var saveMethod = typeof(OrderChangesDialog).GetMethod("SaveAsync", flags)!;
        await (Task)saveMethod.Invoke(dialog, null)!;

        var errorField = typeof(OrderChangesDialog).GetField("_error", flags)!;
        Assert.Equal("Only admins can change orders.", errorField.GetValue(dialog));

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Null(order.MessageFromBuyerOverride); // Untouched in DB
        }

        // Admin saving
        var adminUser = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tophattwaffle"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, EtsyShopManager.Core.Modules.Roles.Admin)
        ], "auth"));
        typeof(OrderChangesDialog).GetProperty("AuthState", flags)!.SetValue(dialog, Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(adminUser)));

        var testDialogInstance = new TestDialogInstance();
        typeof(OrderChangesDialog).GetProperty("Dialog", flags)!.SetValue(dialog, testDialogInstance);

        await (Task)saveMethod.Invoke(dialog, null)!;

        Assert.Null(errorField.GetValue(dialog));
        Assert.True(testDialogInstance.Closed);

        await using (var ctx = db.CreateDbContext())
        {
            var order = await ctx.Orders.SingleAsync();
            Assert.Equal("Edited note by non-admin", order.MessageFromBuyerOverride);
            Assert.Equal("Edited note by non-admin", OrderOverrides.Note(order));
        }
    }

    private static async Task SeedAsync(TestDb db, OrderItem item, string? address = null, string? note = null, string? noteOverride = null)
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
        order.MessageFromBuyer = note;
        order.MessageFromBuyerOverride = noteOverride;
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

    private sealed class TestJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestDialogInstance : MudBlazor.IMudDialogInstance
    {
        public Guid Id => Guid.NewGuid();
        public string ElementId => "";
        public MudBlazor.DialogOptions Options => new();
        public string Title => "";
        public bool Closed { get; private set; }

        public void Close() => Closed = true;
        public void Close(MudBlazor.DialogResult? result) => Closed = true;
        public void Close<T>(T result) => Closed = true;
        public void Cancel() { }
        public void CancelAll() { }
        public Task SetOptionsAsync(MudBlazor.DialogOptions options) => Task.CompletedTask;
        public Task SetTitleAsync(string? title) => Task.CompletedTask;
        public void StateHasChanged() { }
    }

    private sealed class TestAuthStateProvider(System.Security.Claims.ClaimsPrincipal user) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(user));
    }

    private sealed class FragmentWrapper : ComponentBase
    {
        [Parameter] public RenderFragment ChildContent { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<Microsoft.AspNetCore.Components.Authorization.CascadingAuthenticationState>(0);
            builder.AddAttribute(1, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }
}
