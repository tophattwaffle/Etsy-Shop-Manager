using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ListingEditModelTests
{
    private static JsonObject State(string title = "Vase", string state = "active", int images = 1) => new()
    {
        ["state"] = state,
        ["title"] = title,
        ["description"] = "d",
        ["tags"] = new JsonArray("a", "b"),
        ["images"] = new JsonArray(Enumerable.Range(1, images).Select(i => (JsonNode)new JsonObject { ["etsy_image_id"] = i }).ToArray()),
    };

    [Fact]
    public void First_read_becomes_the_base_with_nothing_pending()
    {
        var m = new ListingEditModel(State(), null, null);
        Assert.Empty(m.DriftFields);
        Assert.Empty(m.PendingFields);
        Assert.False(m.HasDraft);
    }

    [Fact]
    public void Editing_back_to_the_agreed_value_removes_the_edit()
    {
        var m = new ListingEditModel(State(), null, null);
        m.Set("title", "New");
        Assert.Equal(["title"], m.PendingFields);
        m.Set("title", "Vase");
        Assert.False(m.HasDraft);
        Assert.Throws<ArgumentException>(() => m.Set("images", new JsonArray()));
    }

    [Fact]
    public void A_tracked_change_on_Etsy_is_drift_and_can_be_kept_or_taken()
    {
        var baseState = State();
        var keep = new ListingEditModel(State(title: "Changed on Etsy"), (JsonObject)baseState.DeepClone(), null);
        Assert.Equal(["title"], keep.DriftFields);
        keep.KeepMine("title");
        Assert.Empty(keep.DriftFields);
        Assert.Equal(["title"], keep.PendingFields); // the next push restores the local title
        Assert.Equal("Vase", (string?)keep.Local("title"));

        var take = new ListingEditModel(State(title: "Changed on Etsy"), (JsonObject)baseState.DeepClone(),
            new JsonObject { ["title"] = "My edit" });
        take.TakeEtsy("title");
        Assert.Empty(take.DriftFields);
        Assert.Empty(take.PendingFields);
        Assert.Equal("Changed on Etsy", (string?)take.Local("title"));
    }

    [Fact]
    public void Untracked_fields_follow_Etsy_without_drift()
    {
        var m = new ListingEditModel(State(state: "expired", images: 3), State(), null);
        Assert.Empty(m.DriftFields);
        Assert.Equal("expired", (string?)m.Base["state"]);
        Assert.Equal(3, m.Base["images"]!.AsArray().Count);
    }

    [Fact]
    public void After_a_push_accepted_edits_are_done_and_rejected_ones_stay()
    {
        var m = new ListingEditModel(State(), null, null);
        m.Set("title", "New");
        m.Set("tags", new JsonArray("x"));

        var notApplied = m.AcceptPushed(State(title: "New")); // Etsy took the title but not the tags

        Assert.Equal(["tags"], notApplied);
        Assert.Equal(["tags"], m.Draft.Select(p => p.Key));
        Assert.Empty(m.DriftFields);
    }
}

public class ListingPushTests
{
    private static async Task<(FakeEtsyShop Shop, int ListingId)> ImportedAsync(Action<JsonObject>? change = null)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        change?.Invoke(listing);
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<Listing> ListingAsync(FakeEtsyShop s, int id)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == id);
    }

    private static async Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        await s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    /// <summary>The managed inventory with the "Small" product's price changed.</summary>
    private static async Task<JsonNode> InventoryWithSmallPriceAsync(FakeEtsyShop s, int id, long amount)
    {
        var editor = await Editor(s).LoadAsync(id);
        var inventory = editor!.Model.Local(ListingFields.Inventory)!.DeepClone();
        var small = inventory["products"]!.AsArray().Single(p => (string?)p!["property_values"]![0]!["values"]![0] == "Small")!;
        small["offerings"]![0]!["price_amount"] = amount;
        return inventory;
    }

    [Fact]
    public async Task Local_edits_survive_reimport_and_a_change_on_Etsy_is_flagged_once()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "My new title" }, "tophattwaffle");

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var l = await ListingAsync(shop, id);
        Assert.True(l.HasDraft);
        Assert.False(l.HasDrift);
        Assert.Contains("My new title", l.DraftJson);
        Assert.Empty(shop.Events.Events.OfType<DriftDetected>());

        shop.ListingJson(1)["description"] = "Edited on etsy.com";
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        l = await ListingAsync(shop, id);
        Assert.True(l.HasDrift);
        var drift = Assert.Single(shop.Events.Events.OfType<DriftDetected>());
        Assert.Equal(["description"], drift.Fields);
        var preview = await Editor(shop).PreviewAsync(id);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("Description"));
    }

    [Fact]
    public async Task Push_sends_exactly_the_edited_fields_and_rebuilds_the_inventory_from_Etsy()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var editor = Editor(shop);
        await editor.SaveAsync(id, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Galaxy vase, large",
            ["tags"] = new JsonArray("vase", "galaxy", "gift"),
            [ListingFields.Inventory] = await InventoryWithSmallPriceAsync(shop, id, 2199),
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Equal(["title", ListingFields.Inventory, "tags"], preview.Changes.Select(c => c.Key));
        Assert.Empty(shop.Writes); // previewing never calls Etsy

        var (pushId, blockers) = await editor.RequestPushAsync(id, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.Equal(PushListingJob.Type, shop.Provider.GetRequiredService<RecordingJobQueue>().Requests.Single().JobType);
        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal(2, shop.Writes.Count);
        var patch = shop.Writes[0];
        Assert.Equal(("PATCH", "/v3/application/shops/42/listings/1"), (patch.Method, patch.Path));
        Assert.Equal("title=Galaxy+vase%2C+large&tags=vase%2Cgalaxy%2Cgift", patch.Body);

        var put = shop.Writes[1];
        Assert.Equal(("PUT", "/v3/application/listings/1/inventory"), (put.Method, put.Path));
        // Exactly Etsy's inventory with only the Small price changed (null fields are left out, not sent as null).
        var expected = JsonNode.Parse("""
            {"products":[
              {"sku":"SKU1-S","property_values":[{"property_id":100,"value_ids":[],"property_name":"Size","values":["Small"]}],
               "offerings":[{"price":21.99,"quantity":5,"is_enabled":true}]},
              {"sku":"SKU1-L","property_values":[{"property_id":100,"value_ids":[],"property_name":"Size","values":["Large"]}],
               "offerings":[{"price":29.99,"quantity":5,"is_enabled":true}]}],
             "price_on_property":[100],"quantity_on_property":[],"sku_on_property":[100],"readiness_state_on_property":[]}
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(put.Body)), put.Body);

        var l = await ListingAsync(shop, id);
        Assert.False(l.HasDraft);
        Assert.False(l.HasDrift);
        Assert.Equal("Galaxy vase, large", l.Title);
        Assert.Empty(shop.Events.Events.OfType<DriftDetected>()); // the re-read after the push isn't drift
        Assert.Single(shop.Events.Events.OfType<ListingPushed>());
        await using var db = shop.Db.CreateDbContext();
        var push = await db.ListingPushes.Include(p => p.Snapshot).SingleAsync();
        Assert.Equal(ListingPushStatus.Succeeded, push.Status);
        Assert.Contains("\"title\":\"Galaxy vase\"", push.Snapshot!.ManagedJson); // what Etsy had before
    }

    [Fact]
    public async Task Etsy_html_encoding_is_decoded_and_pushed_text_round_trips()
    {
        // Etsy returns text HTML-encoded; the app keeps plain text and pushes it as written.
        var (shop, id) = await ImportedAsync(l => l["description"] = "Each layout&#39;s holes are not in a &quot;uniform&quot; spot");
        using var _ = shop;
        Assert.Equal("Each layout's holes are not in a \"uniform\" spot", (await ListingAsync(shop, id)).Description);

        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["description"] = "It's \"new\"" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal("description=It%27s+%22new%22", Assert.Single(shop.Writes).Body); // plain text, not &#39;
        var l = await ListingAsync(shop, id);
        Assert.False(l.HasDraft); // Etsy's encoded copy reads back as the same text
        Assert.Equal("It's \"new\"", l.Description);
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Push_stops_without_writing_when_the_listing_changed_on_Etsy_meanwhile()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Mine" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        shop.ListingJson(1)["title"] = "Theirs";
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_900;

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunPushAsync(shop, pushId!.Value));

        Assert.Empty(shop.Writes);
        var l = await ListingAsync(shop, id);
        Assert.True(l.HasDrift);
        Assert.True(l.HasDraft);
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(ListingPushStatus.Failed, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Rejected_write_fails_the_push_and_keeps_the_edits()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Mine" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        shop.FailWritesWith = System.Net.HttpStatusCode.BadRequest;

        await Assert.ThrowsAsync<EtsyShopManager.Etsy.EtsyApiException>(() => RunPushAsync(shop, pushId!.Value));

        Assert.True((await ListingAsync(shop, id)).HasDraft);
        await using var db = shop.Db.CreateDbContext();
        var push = await db.ListingPushes.SingleAsync();
        Assert.Equal(ListingPushStatus.Failed, push.Status);
        Assert.Contains("400", push.Error);
    }

    [Fact]
    public async Task Retrying_a_finished_push_job_sends_nothing()
    {
        // "Retry" on the Jobs page re-runs the job with the same push id. Pushes go out only after a reviewed preview,
        // so a push that already finished must not send whatever the draft holds now.
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Mine" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        shop.FailWritesWith = System.Net.HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<EtsyShopManager.Etsy.EtsyApiException>(() => RunPushAsync(shop, pushId!.Value));
        shop.FailWritesWith = null;
        shop.Writes.Clear();
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Changed after the preview" }, "tophattwaffle");

        var ex = await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunPushAsync(shop, pushId!.Value));

        Assert.Empty(shop.Writes);
        Assert.Contains("push again from the editor", ex.Message);
        await using var db = shop.Db.CreateDbContext();
        var push = await db.ListingPushes.SingleAsync();
        Assert.Equal(ListingPushStatus.Failed, push.Status);
        Assert.Contains("400", push.Error); // the original outcome is kept
    }

    [Fact]
    public async Task A_half_written_push_is_reconciled_so_its_own_changes_are_not_drift()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Mine",
            [ListingFields.Inventory] = await InventoryWithSmallPriceAsync(shop, id, 2199),
        }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        shop.FailInventoryWritesWith = System.Net.HttpStatusCode.BadRequest; // the title goes through, the prices don't

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunPushAsync(shop, pushId!.Value));

        Assert.Equal(["PATCH", "PUT"], shop.Writes.Select(w => w.Method));
        await using (var db = shop.Db.CreateDbContext())
        {
            var push = await db.ListingPushes.SingleAsync();
            Assert.Equal(ListingPushStatus.PartlyApplied, push.Status);
            Assert.Contains("Applied before the error: Title", push.Error);
        }
        shop.FailInventoryWritesWith = null;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var l = await ListingAsync(shop, id);
        Assert.Equal("Mine", l.Title);
        Assert.False(l.HasDrift); // the title change was ours, not a change on etsy.com
        Assert.Empty(shop.Events.Events.OfType<DriftDetected>());
        var preview = await Editor(shop).PreviewAsync(id);
        Assert.Equal([ListingFields.Inventory], preview.Changes.Select(c => c.Key)); // only the prices are left to push
        Assert.Empty(preview.Blockers);
    }

    [Fact]
    public async Task A_push_whose_job_was_cancelled_does_not_block_the_listing()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Mine" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        long jobId;
        await using (var db = shop.Db.CreateDbContext())
            jobId = (await db.ListingPushes.SingleAsync(p => p.Id == pushId)).JobId!.Value;

        await using (var db = shop.Db.CreateDbContext())
        {
            db.Jobs.Add(new Job { Id = jobId, JobType = PushListingJob.Type, Status = JobStatus.Pending });
            await db.SaveChangesAsync();
        }
        Assert.Contains((await Editor(shop).PreviewAsync(id)).Blockers, b => b.Contains("already queued or running"));

        // Cancelled on the Jobs page before it ran: the push never finishes, but the listing must not stay blocked.
        await using (var db = shop.Db.CreateDbContext())
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled));

        Assert.True((await Editor(shop).PreviewAsync(id)).CanPush);
    }

    [Fact]
    public async Task Preview_blocks_invalid_edits()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var inventory = await InventoryWithSmallPriceAsync(shop, id, 0);
        inventory["price_on_property"] = new JsonArray(); // one price for all, but they differ now
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?>
        {
            ["title"] = new string('x', 141),
            ["tags"] = new JsonArray(Enumerable.Range(0, 14).Select(i => (JsonNode)$"t{i}").ToArray()),
            [ListingFields.Inventory] = inventory,
        }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);

        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("141 characters"));
        Assert.Contains(preview.Blockers, b => b.Contains("14 tags"));
        Assert.Contains(preview.Blockers, b => b.Contains("more than zero"));
        Assert.Contains(preview.Blockers, b => b.Contains("same price"));
        Assert.Null((await Editor(shop).RequestPushAsync(id, "tophattwaffle")).PushId);
    }

    [Fact]
    public async Task A_listing_read_by_an_older_version_must_be_refreshed_before_pushing()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "New" }, "tophattwaffle");
        await using (var db = shop.Db.CreateDbContext())
            await db.ListingEtsyStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.ManagedVersion, 1));

        Assert.Contains((await Editor(shop).PreviewAsync(id)).Blockers, b => b.Contains("older version"));

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        Assert.True((await Editor(shop).PreviewAsync(id)).CanPush);
    }

    [Fact]
    public async Task Restoring_a_snapshot_puts_the_old_values_in_the_draft()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "Pushed title" }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);
        int snapshotId;
        await using (var db = shop.Db.CreateDbContext())
            snapshotId = (await db.ListingPushes.SingleAsync()).SnapshotId!.Value;

        await Editor(shop).RestoreSnapshotAsync(snapshotId, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        var change = Assert.Single(preview.Changes);
        Assert.Equal(("title", "Pushed title", "Galaxy vase"), (change.Key, (string?)change.From, (string?)change.To));
    }

    [Fact]
    public void Form_mapping_formats_values_for_Etsy()
    {
        var local = new JsonObject
        {
            ["title"] = "T", ["is_supply"] = false, ["item_weight"] = 1.5m, ["shop_section_id"] = null,
            ["materials"] = new JsonArray("PLA", "paint"), [ListingFields.Inventory] = new JsonObject(),
        };

        var form = ListingFormMapper.ToForm(local, ["title", "is_supply", "item_weight", "shop_section_id", "materials", ListingFields.Inventory]);

        Assert.Equal(
            ["title=T", "is_supply=false", "item_weight=1.5", "shop_section_id=0", "materials=PLA,paint"],
            form.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}

public class StockRefillTests
{
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync(int smallQty, int largeQty, bool quantityVaries,
        string state = "active", bool enabled = true)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase", state: state);
        var products = listing["inventory"]!["products"]!.AsArray();
        products[0]!["offerings"]![0]!["quantity"] = smallQty;
        products[1]!["offerings"]![0]!["quantity"] = largeQty;
        listing["inventory"]!["quantity_on_property"] = quantityVaries ? new JsonArray(100) : new JsonArray();
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        int id;
        await using (var db = shop.Db.CreateDbContext()) id = (await db.Listings.SingleAsync()).Id;
        await shop.Provider.GetRequiredService<StockRefiller>().SaveSettingsAsync(id, enabled, 5, 999, "tophattwaffle");
        return (shop, id);
    }

    private static StockRefiller Refiller(FakeEtsyShop s) => s.Provider.GetRequiredService<StockRefiller>();

    [Fact]
    public async Task Refills_only_the_low_variation_and_changes_nothing_else()
    {
        var (shop, id) = await SetupAsync(smallQty: 3, largeQty: 50, quantityVaries: true);
        using var _ = shop;
        var before = JsonNode.Parse(shop.ListingJson(1)["inventory"]!.ToJsonString())!;

        var outcome = await Refiller(shop).RefillListingAsync(id, CancellationToken.None);

        Assert.Equal(["Small: 3 → 999"], outcome.Changes);
        var put = Assert.Single(shop.Writes);
        var body = JsonNode.Parse(put.Body)!;
        var offerings = body["products"]!.AsArray().Select(p => p!["offerings"]![0]!).ToList();
        Assert.Equal([999, 50], offerings.Select(o => (int)o["quantity"]!));
        Assert.Equal([19.99m, 29.99m], offerings.Select(o => (decimal)o["price"]!));
        Assert.Equal(["SKU1-S", "SKU1-L"], body["products"]!.AsArray().Select(p => (string?)p!["sku"]));
        Assert.True(JsonNode.DeepEquals(before["products"]![0]!["property_values"], body["products"]![0]!["property_values"]));
        Assert.Equal("[100]", body["price_on_property"]!.ToJsonString());
        Assert.Single(shop.Events.Events.OfType<StockRefilled>());
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal([999, 50], await db.ListingOfferings.OrderBy(o => o.Product.Sku == "SKU1-L").Select(o => o.Quantity).ToListAsync());
    }

    [Theory]
    [InlineData(true, 999 + 50)] // quantity varies by size: Etsy's listing total is the sum of the variations
    [InlineData(false, 999)]     // one shared quantity: Etsy reports that quantity
    public async Task A_refill_updates_the_listing_stock_shown_in_the_app(bool quantityVaries, int expected)
    {
        var (shop, id) = await SetupAsync(smallQty: 3, largeQty: quantityVaries ? 50 : 3, quantityVaries: quantityVaries);
        using var _ = shop;

        await Refiller(shop).RefillListingAsync(id, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(expected, (await db.Listings.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task Shared_quantity_refills_all_variations_together()
    {
        var (shop, id) = await SetupAsync(smallQty: 3, largeQty: 3, quantityVaries: false);
        using var _ = shop;

        var outcome = await Refiller(shop).RefillListingAsync(id, CancellationToken.None);

        // Etsy's inventory format repeats the shared number on every variation; the app treats it as one stock.
        var body = JsonNode.Parse(Assert.Single(shop.Writes).Body)!;
        Assert.All(body["products"]!.AsArray(), p => Assert.Equal(999, (int)p!["offerings"]![0]!["quantity"]!));
        Assert.Equal(["stock: 3 → 999"], outcome.Changes);
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal("stock: 3 → 999", (await db.ListingRefills.SingleAsync()).LastResult);
    }

    [Fact]
    public async Task A_refill_Etsy_rejects_is_shown_on_the_listing_and_not_retried()
    {
        var (shop, id) = await SetupAsync(smallQty: 3, largeQty: 50, quantityVaries: true);
        using var _ = shop;
        shop.FailWritesWith = System.Net.HttpStatusCode.BadRequest;

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => Refiller(shop).RefillListingAsync(id, CancellationToken.None));

        await using var db = shop.Db.CreateDbContext();
        var settings = await db.ListingRefills.SingleAsync();
        Assert.Contains("Etsy rejected the refill", settings.Problem);
        Assert.Equal(settings.Problem, settings.LastResult);
        Assert.NotNull(settings.LastCheckedAtUtc);
        Assert.Null(settings.LastRefilledAtUtc);
    }

    [Theory]
    [InlineData(10, 10, "active", true)]  // above the threshold
    [InlineData(3, 3, "inactive", true)]  // not active: refilling would not help and must not reactivate it
    [InlineData(3, 3, "active", false)]   // refill off
    public async Task Leaves_the_listing_alone(int small, int large, string state, bool enabled)
    {
        var (shop, id) = await SetupAsync(small, large, quantityVaries: true, state: state, enabled: enabled);
        using var _ = shop;

        var outcome = await Refiller(shop).RefillListingAsync(id, CancellationToken.None);

        Assert.Empty(shop.Writes);
        Assert.Empty(outcome.Changes);
        if (state != "active") Assert.Contains("inactive", outcome.Problem);
    }

    [Fact]
    public async Task A_new_order_queues_a_refill_for_its_refill_enabled_listings()
    {
        var (shop, id) = await SetupAsync(3, 3, quantityVaries: true);
        using var _ = shop;
        await using (var db = shop.Db.CreateDbContext())
        {
            db.Orders.Add(new Order
            {
                ShopId = FakeEtsyShop.ShopId, EtsyReceiptId = 77, Status = "paid", IsPaid = true,
                Items = [new OrderItem { EtsyTransactionId = 1, EtsyListingId = 1, Quantity = 1 }],
            });
            await db.SaveChangesAsync();
        }
        var orderId = await shop.Db.CreateDbContext().Orders.Select(o => o.Id).SingleAsync();

        await new RefillOnOrderReceived(shop.Db, shop.Provider.GetRequiredService<RefillQueue>())
            .HandleAsync(new OrderReceived(FakeEtsyShop.ShopId, orderId, 77), CancellationToken.None);

        var job = Assert.Single(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
        Assert.Equal(RefillListingJob.Type, job.JobType);
        Assert.Equal(id, ((RefillPayload)job.Payload!).ListingId);
    }

    [Fact]
    public async Task Sweep_finds_low_stock_from_one_bulk_read()
    {
        var (shop, id) = await SetupAsync(3, 50, quantityVaries: true);
        using var _ = shop;
        shop.Api.Requests.Clear();

        var due = await Refiller(shop).FindLowStockAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.Equal([id], due);
        Assert.Single(shop.RequestedPaths); // one getListingsByShop page with inventories
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        var (shop, id) = await SetupAsync(10, 10, quantityVaries: true);
        using var _ = shop;
        await Assert.ThrowsAsync<ArgumentException>(() => Refiller(shop).SaveSettingsAsync(id, true, 5, 5, "tophattwaffle"));
        await Assert.ThrowsAsync<ArgumentException>(() => Refiller(shop).SaveSettingsAsync(id, true, 5, 1000, "tophattwaffle"));
        await Assert.ThrowsAsync<ArgumentException>(() => Refiller(shop).SaveSettingsAsync(id, true, 0, 10, "tophattwaffle"));
    }
}

public class BulkRefillTests
{
    /// <summary>Three listings: active and low (3), active and well stocked (50, already on: below 10, refill to 500), inactive and low.</summary>
    private static async Task<(FakeEtsyShop Shop, int Low, int Stocked, int Inactive)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Low vase", quantity: 3));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Stocked vase", quantity: 50));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "Inactive vase", state: "inactive", quantity: 3));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);
        await Refiller(shop).SaveSettingsAsync(ids[2], true, 10, 500, "tophattwaffle");
        return (shop, ids[1], ids[2], ids[3]);
    }

    private static StockRefiller Refiller(FakeEtsyShop s) => s.Provider.GetRequiredService<StockRefiller>();

    private static System.Security.Claims.ClaimsPrincipal User(string role) => new(new System.Security.Claims.ClaimsIdentity(
    [
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tophattwaffle"),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
    ], "test"));

    private static async Task<Dictionary<int, (bool, int, int)>> SettingsAsync(FakeEtsyShop s)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.ListingRefills.ToDictionaryAsync(r => r.ListingId, r => (r.Enabled, r.Threshold, r.RefillTo));
    }

    [Fact]
    public async Task Bulk_apply_changes_only_the_given_fields_and_sends_nothing_to_Etsy()
    {
        var (shop, low, stocked, inactive) = await SetupAsync();
        using var _ = shop;

        var changed = await Refiller(shop).ApplyBulkAsync([low, stocked, inactive], new BulkRefillChange(true, null, null),
            User(EtsyShopManager.Core.Modules.Roles.Editor));

        Assert.Equal(2, changed); // the stocked listing was already on
        var settings = await SettingsAsync(shop);
        Assert.Equal((true, 5, 999), settings[low]); // new rows start from the defaults
        Assert.Equal((true, 10, 500), settings[stocked]); // its own threshold and refill-to are kept
        Assert.Equal((true, 5, 999), settings[inactive]);
        Assert.Empty(shop.Writes);
        Assert.Empty(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests); // refills wait for the next check

        await Refiller(shop).ApplyBulkAsync([low, stocked], new BulkRefillChange(null, null, 200), User(EtsyShopManager.Core.Modules.Roles.Admin));
        settings = await SettingsAsync(shop);
        Assert.Equal((true, 5, 200), settings[low]);
        Assert.Equal((true, 10, 200), settings[stocked]);
    }

    [Fact]
    public async Task Preview_counts_what_the_next_check_would_refill()
    {
        var (shop, low, stocked, inactive) = await SetupAsync();
        using var _ = shop;

        var preview = await Refiller(shop).PreviewBulkAsync([low, stocked, inactive], new BulkRefillChange(true, 5, null));

        Assert.Equal(3, preview.Selected);
        Assert.Equal(3, preview.Changing); // the stocked listing's threshold goes from 10 to 5
        Assert.Equal(1, preview.WouldRefill); // only the active listing below 5
        Assert.Equal(1, preview.NotActive); // the inactive one is on but won't be refilled
        Assert.Empty(preview.Problems);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Bulk_values_are_validated_against_each_listings_settings()
    {
        var (shop, low, stocked, _) = await SetupAsync();
        using var _s = shop;
        var change = new BulkRefillChange(null, 20, null); // below both refill-to values (999 and 500)…
        Assert.Empty((await Refiller(shop).PreviewBulkAsync([low, stocked], change)).Problems);

        change = new BulkRefillChange(null, 600, null); // …but 600 is not below the stocked listing's refill-to of 500
        var preview = await Refiller(shop).PreviewBulkAsync([low, stocked], change);
        Assert.Contains(preview.Problems, p => p.Contains("Refill-to must be more than the threshold"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Refiller(shop).ApplyBulkAsync([low, stocked], change, User(EtsyShopManager.Core.Modules.Roles.Editor)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Refiller(shop).ApplyBulkAsync([low], new BulkRefillChange(null, null, 1000), User(EtsyShopManager.Core.Modules.Roles.Editor)));

        var settings = await SettingsAsync(shop);
        Assert.Equal((true, 10, 500), settings[stocked]); // nothing saved
        Assert.False(settings.ContainsKey(low));
    }

    [Fact]
    public async Task Viewers_cannot_bulk_edit()
    {
        var (shop, low, _, _) = await SetupAsync();
        using var _s = shop;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Refiller(shop).ApplyBulkAsync([low], new BulkRefillChange(true, null, null), User(EtsyShopManager.Core.Modules.Roles.Viewer)));

        Assert.False((await SettingsAsync(shop)).ContainsKey(low));
    }

    [Fact]
    public async Task Select_all_takes_every_listing_matching_the_filter_across_pages()
    {
        var (shop, low, stocked, inactive) = await SetupAsync();
        using var _ = shop;
        var queries = shop.Provider.GetRequiredService<EtsyShopManager.Web.Modules.Listings.ListingQueries>();

        var active = await queries.SearchIdsAsync(new EtsyShopManager.Web.Modules.Listings.ListingFilter { States = ["active"], Take = 1 });
        var search = await queries.SearchIdsAsync(new EtsyShopManager.Web.Modules.Listings.ListingFilter { Text = "inactive" });

        Assert.Equal([low, stocked], active.Order());
        Assert.Equal([inactive], search);
    }
}
