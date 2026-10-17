using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class PhotoOrderTestTests
{
    /// <summary>One listing with three photos (ids 11, 12, 13 in that order).</summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync(string state = "inactive")
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase", state: state);
        listing["images"] = new JsonArray(FakeEtsyShop.Image(1, 11, 1), FakeEtsyShop.Image(1, 12, 2), FakeEtsyShop.Image(1, 13, 3));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static PhotoOrderTester Tester(FakeEtsyShop s) => s.Provider.GetRequiredService<PhotoOrderTester>();

    private static Task RunAsync(FakeEtsyShop s, int testId) =>
        s.Provider.GetRequiredService<PhotoOrderTestJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PhotoOrderTestJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhotoOrderTestPayload(testId), JobJson.Options),
        }, CancellationToken.None);

    private static async Task<PhotoOrderTest> TestAsync(FakeEtsyShop s)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.PhotoOrderTests.SingleAsync();
    }

    private static List<long> Order(FakeEtsyShop s) =>
        [.. s.ListingJson(1)["images"]!.AsArray().Select(i => (long)i!["listing_image_id"]!)];

    [Fact]
    public async Task A_run_moves_photos_one_call_at_a_time_and_records_every_read_back()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, problem) = await Tester(shop).RequestAsync(id, [new(13, 1, false, true), new(11, 3, false, false)], "tophattwaffle");
        Assert.Null(problem);

        await RunAsync(shop, testId!.Value);

        Assert.Equal(["POST", "POST"], shop.Writes.Select(w => w.Method));
        Assert.Equal("listing_image_id=13&rank=1&overwrite=false&alt_text=Photo 3", shop.Writes[0].Body); // current alt text kept
        Assert.Equal("listing_image_id=11&rank=3&overwrite=false", shop.Writes[1].Body);
        Assert.Equal([13L, 12L, 11L], Order(shop));

        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Completed, test.Status);
        Assert.Equal("Completed 2 step(s); 2 left the order as expected.", test.Outcome);
        var result = JsonNode.Parse(test.ResultJson!)!;
        Assert.Equal("[11,12,13]", result["before"]!.ToJsonString());
        Assert.Equal("[13,11,12]", result["steps"]![0]!["read_now"]!.ToJsonString());
        Assert.Equal("[13,11,12]", result["steps"]![0]!["read_after_20s"]!.ToJsonString());
        Assert.Equal("[13,12,11]", result["final_after_120s"]!.ToJsonString());
        Assert.NotNull(test.SnapshotId);
        Assert.Contains(TimeSpan.FromSeconds(PhotoOrderTester.FinalWaitSeconds), shop.Time.Delays);
    }

    [Fact]
    public async Task It_stops_at_the_first_rejection_and_records_what_Etsy_left()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, true, false), new(11, 3, false, false)], "tophattwaffle");
        shop.FailImageMovesWith = "The ListingImages for this Listing are in an inconsistent order.";

        await RunAsync(shop, testId!.Value);

        Assert.Single(shop.Writes); // the second step was never sent
        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Stopped, test.Status);
        Assert.Contains("Stopped at step 1: Etsy answered 400", test.Outcome);
        var step = JsonNode.Parse(test.ResultJson!)!["steps"]![0]!;
        Assert.Contains("inconsistent order", (string?)step["answer"]);
        Assert.Equal("[11,12,13]", step["read_after_rejection"]!.ToJsonString());
    }

    [Theory]
    [InlineData("active")]
    [InlineData("sold_out")]
    public async Task A_public_listing_is_refused_before_anything_is_written(string state)
    {
        var (shop, id) = await SetupAsync(state);
        using var _ = shop;

        var (testId, problem) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");

        Assert.Null(testId);
        Assert.Contains("Only a draft or inactive listing", problem);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_listing_that_went_live_since_the_request_is_refused_by_the_run()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");
        shop.ListingJson(1)["state"] = "active"; // activated on etsy.com before the job ran

        await RunAsync(shop, testId!.Value);

        Assert.Empty(shop.Writes);
        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Refused, test.Status);
        Assert.Contains("the listing is active on Etsy", test.Outcome);
    }

    [Fact]
    public async Task A_run_is_never_repeated_or_resumed()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");
        await RunAsync(shop, testId!.Value);
        shop.Writes.Clear();

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunAsync(shop, testId.Value)); // "Retry" on the Jobs page
        Assert.Empty(shop.Writes);

        await using (var db = shop.Db.CreateDbContext())
            await db.PhotoOrderTests.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, PhotoOrderTestStatus.Running));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunAsync(shop, testId.Value)); // after a restart
        Assert.Empty(shop.Writes);
        Assert.Contains("interrupted by a restart", (await TestAsync(shop)).Outcome);
    }

    [Fact]
    public void The_review_lists_exactly_what_will_be_sent()
    {
        var calls = PhotoOrderTester.Calls(42, 1, [new(13, 1, true, true)], new Dictionary<long, string?> { [13] = "Vase" });

        Assert.Contains("1. POST /v3/application/shops/42/listings/1/images  listing_image_id=13, rank=1, overwrite=true, alt_text=Vase" +
                        "  (stop here if Etsy rejects it)", calls);
        Assert.StartsWith("GET /v3/application/listings/1 (check it is still draft or inactive", calls[0]);
    }
}
