using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class PendingChangesTests
{
    /// <summary>Three imported listings: Galaxy vase gets a new title, Moon vase too but its tags are then changed on etsy.com,
    /// and Star vase is left alone.</summary>
    private static async Task<(FakeEtsyShop Shop, int Galaxy, int Moon, int Star)> EditedAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Moon vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "Star vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var ids = await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(ids[1], new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, large" }, "tophattwaffle");
        await editor.SaveAsync(ids[2], new Dictionary<string, JsonNode?> { ["title"] = "Moon vase, large" }, "tophattwaffle");
        shop.ListingJson(2)["tags"] = new JsonArray("vase", "etsy edit");
        shop.ListingJson(2)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        return (shop, ids[1], ids[2], ids[3]);
    }

    private static PendingChangesService Pending(FakeEtsyShop s) => s.Provider.GetRequiredService<PendingChangesService>();

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "tophattwaffle"), new Claim(ClaimTypes.Role, role)], "test"));

    [Fact]
    public async Task Lists_each_listing_with_changes_and_blocks_the_ones_changed_on_Etsy()
    {
        var (shop, galaxy, moon, _) = await EditedAsync();
        using var _s = shop;

        Assert.Equal(2, await Pending(shop).CountAsync());
        var rows = await Pending(shop).ListAsync();

        Assert.Equal([galaxy, moon], rows.Select(r => r.ListingId).Order());
        var ready = rows.Single(r => r.ListingId == galaxy);
        Assert.True(ready.CanPush, string.Join("; ", ready.Preview.Blockers));
        var title = Assert.Single(ready.Changes);
        Assert.Equal(("Title", "Galaxy vase", "Galaxy vase, large"), (title.Label, title.From, title.To));
        Assert.Equal("tophattwaffle", ready.EditedBy);

        var blocked = rows.Single(r => r.ListingId == moon);
        Assert.False(blocked.CanPush);
        Assert.Contains(blocked.Preview.Blockers, b => b.Contains("Changed on Etsy") && b.Contains("Tags"));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Pushing_the_selected_listings_is_each_ones_normal_push_and_blocked_ones_stay()
    {
        var (shop, galaxy, moon, _) = await EditedAsync();
        using var _s = shop;
        var rows = await Pending(shop).ListAsync();

        var results = await Pending(shop).PushAsync(rows.ToDictionary(r => r.ListingId, r => r.Reviewed), User(Roles.Editor));

        var pushed = results.Single(r => r.ListingId == galaxy);
        Assert.NotNull(pushed.PushId);
        var refused = results.Single(r => r.ListingId == moon);
        Assert.Null(refused.PushId);
        Assert.Contains(refused.Problems, p => p.Contains("Changed on Etsy"));
        var job = Assert.Single(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
        Assert.Equal(PushListingJob.Type, job.JobType);

        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushed.PushId!.Value), JobJson.Options),
        }, CancellationToken.None);

        var write = Assert.Single(shop.Writes);
        Assert.EndsWith("/listings/1", write.Path);
        Assert.Equal("title=Galaxy+vase%2C+large", write.Body); // only the field that changed
        // Once Etsy has it, the listing is no longer pending; the blocked one still is.
        Assert.Equal([moon], (await Pending(shop).ListAsync()).Select(r => r.ListingId));
    }

    [Fact]
    public async Task A_listing_edited_again_after_the_review_is_not_pushed()
    {
        var (shop, galaxy, _, _) = await EditedAsync();
        using var _s = shop;
        var reviewed = (await Pending(shop).ListAsync()).Single(r => r.ListingId == galaxy);

        await shop.Provider.GetRequiredService<ListingEditor>()
            .SaveAsync(galaxy, new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, huge" }, "someone else");
        var result = Assert.Single(await Pending(shop).PushAsync(new Dictionary<int, string> { [galaxy] = reviewed.Reviewed }, User(Roles.Admin)));

        Assert.Null(result.PushId);
        Assert.Contains("different from the ones you reviewed", Assert.Single(result.Problems));
        Assert.Empty(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
    }

    [Fact]
    public async Task Viewers_cannot_push()
    {
        var (shop, galaxy, _, _) = await EditedAsync();
        using var _s = shop;
        var reviewed = (await Pending(shop).ListAsync()).Single(r => r.ListingId == galaxy);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Pending(shop).PushAsync(new Dictionary<int, string> { [galaxy] = reviewed.Reviewed }, User(Roles.Viewer)));
        Assert.Empty(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests);
    }
}
