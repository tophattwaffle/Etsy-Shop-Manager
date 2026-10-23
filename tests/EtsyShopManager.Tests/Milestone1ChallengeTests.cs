using System.Security.Cryptography;
using System.Text.Json;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Tests;

public class Milestone1ChallengeTests
{
    [Fact]
    public async Task SQLite_in_memory_migrations_apply_cleanly_and_support_ListingVideos_and_SkuPattern()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateDbContext();

        // 1. Verify schema constraints and columns exist via SQLite pragma
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(ListingVideos);";
        var videoColumns = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                videoColumns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("Id", videoColumns);
        Assert.Contains("ListingId", videoColumns);
        Assert.Contains("EtsyVideoId", videoColumns);
        Assert.Contains("Height", videoColumns);
        Assert.Contains("Width", videoColumns);
        Assert.Contains("VideoState", videoColumns);
        Assert.Contains("SourceUrl", videoColumns);
        Assert.Contains("SourceThumbUrl", videoColumns);
        Assert.Contains("FileName", videoColumns);
        Assert.Contains("ThumbFileName", videoColumns);
        Assert.Contains("ByteSize", videoColumns);
        Assert.Contains("DownloadedAtUtc", videoColumns);
        Assert.Contains("DownloadError", videoColumns);

        cmd.CommandText = "PRAGMA table_info(Listings);";
        var listingColumns = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                listingColumns.Add(reader.GetString(1));
            }
        }
        Assert.Contains("SkuPattern", listingColumns);

        // 2. Insert Listing with SkuPattern and associated ListingVideo
        var shop = new Shop { EtsyShopId = 12345, Name = "Test Shop", ConnectedAtUtc = DateTime.UtcNow };
        ctx.Shops.Add(shop);
        await ctx.SaveChangesAsync();

        var listing = new Listing
        {
            ShopId = shop.Id,
            EtsyListingId = 999001,
            Title = "Handmade Ceramic Mug",
            SkuPattern = "MUG-{color}-{size}",
            Description = "A nice mug.",
            State = "active",
            PriceAmount = 2500,
            PriceDivisor = 100,
        };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        var video = new ListingVideo
        {
            ListingId = listing.Id,
            EtsyVideoId = 888111,
            Height = 1080,
            Width = 1920,
            VideoState = "active",
            SourceUrl = "https://etsy.com/video/888111.mp4",
            SourceThumbUrl = "https://etsy.com/video/888111_thumb.jpg",
            FileName = "mug_video.mp4",
            ThumbFileName = "mug_thumb.jpg",
            ByteSize = 5000000,
            DownloadedAtUtc = DateTime.UtcNow,
        };
        ctx.ListingVideos.Add(video);
        await ctx.SaveChangesAsync();

        // 3. Query back via EF Core with navigation property
        var loaded = await ctx.Listings
            .Include(l => l.Videos)
            .SingleAsync(l => l.Id == listing.Id);

        Assert.Equal("MUG-{color}-{size}", loaded.SkuPattern);
        Assert.Single(loaded.Videos);
        var loadedVideo = loaded.Videos[0];
        Assert.Equal(888111, loadedVideo.EtsyVideoId);
        Assert.Equal(1080, loadedVideo.Height);
        Assert.Equal(1920, loadedVideo.Width);
        Assert.Equal("active", loadedVideo.VideoState);
        Assert.Equal("https://etsy.com/video/888111.mp4", loadedVideo.SourceUrl);
        Assert.Equal("mug_video.mp4", loadedVideo.FileName);
        Assert.Equal(5000000, loadedVideo.ByteSize);
    }

    [Fact]
    public async Task Unique_constraint_on_ListingId_and_EtsyVideoId_is_enforced()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateDbContext();

        var shop = new Shop { EtsyShopId = 12345, Name = "Test Shop", ConnectedAtUtc = DateTime.UtcNow };
        ctx.Shops.Add(shop);
        var listing = new Listing { Shop = shop, EtsyListingId = 999002, Title = "Item 2", State = "active" };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        ctx.ListingVideos.Add(new ListingVideo
        {
            ListingId = listing.Id,
            EtsyVideoId = 777001,
            SourceUrl = "https://example.com/v1.mp4",
        });
        await ctx.SaveChangesAsync();

        // Attempt duplicate (ListingId, EtsyVideoId)
        ctx.ListingVideos.Add(new ListingVideo
        {
            ListingId = listing.Id,
            EtsyVideoId = 777001,
            SourceUrl = "https://example.com/v2.mp4",
        });

        await Assert.ThrowsAsync<DbUpdateException>(async () => await ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Cascade_delete_removes_videos_when_listing_is_deleted()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateDbContext();

        var shop = new Shop { EtsyShopId = 12345, Name = "Test Shop", ConnectedAtUtc = DateTime.UtcNow };
        ctx.Shops.Add(shop);
        var listing = new Listing
        {
            Shop = shop,
            EtsyListingId = 999003,
            Title = "Item 3",
            State = "active",
            Videos =
            [
                new ListingVideo { EtsyVideoId = 555001, SourceUrl = "https://example.com/v1.mp4" },
                new ListingVideo { EtsyVideoId = 555002, SourceUrl = "https://example.com/v2.mp4" },
            ]
        };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        Assert.Equal(2, await ctx.ListingVideos.CountAsync(v => v.ListingId == listing.Id));

        // Delete listing
        ctx.Listings.Remove(listing);
        await ctx.SaveChangesAsync();

        Assert.Equal(0, await ctx.ListingVideos.CountAsync(v => v.ListingId == listing.Id));
    }

    [Fact]
    public void ManagedListingState_CurrentVersion_is_6()
    {
        Assert.Equal(6, ManagedListingState.CurrentVersion);
    }

    [Fact]
    public void ManagedListingState_hash_is_deterministic_when_video_is_null()
    {
        var listing1 = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos = [],
        };

        var listing2 = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos = [],
        };

        var state1 = ManagedListingState.From(listing1);
        var state2 = ManagedListingState.From(listing2);

        Assert.Null(state1.Video);
        Assert.Null(state2.Video);

        var json1 = state1.ToCanonicalJson();
        var json2 = state2.ToCanonicalJson();

        Assert.Contains("\"video\":null", json1);
        Assert.Equal(json1, json2);

        var hash1 = ManagedListingState.Hash(json1);
        var hash2 = ManagedListingState.Hash(json2);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void ManagedListingState_video_presence_and_mutations_change_hash_for_drift()
    {
        var baseListing = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos = [],
        };

        var hashNoVideo = ManagedListingState.Hash(ManagedListingState.From(baseListing).ToCanonicalJson());

        var listingWithVideo = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos =
            [
                new ListingVideo
                {
                    EtsyVideoId = 101,
                    VideoState = "active",
                    Width = 1920,
                    Height = 1080,
                    SourceUrl = "https://example.com/video.mp4",
                    FileName = "local.mp4",
                }
            ],
        };

        var stateWithVideo = ManagedListingState.From(listingWithVideo);
        Assert.NotNull(stateWithVideo.Video);
        Assert.Equal(101, stateWithVideo.Video.EtsyVideoId);
        Assert.Equal("active", stateWithVideo.Video.VideoState);
        Assert.Equal(1920, stateWithVideo.Video.Width);
        Assert.Equal(1080, stateWithVideo.Video.Height);

        var hashWithVideo = ManagedListingState.Hash(stateWithVideo.ToCanonicalJson());
        Assert.NotEqual(hashNoVideo, hashWithVideo);

        // 1. Changing EtsyVideoId changes hash
        listingWithVideo.Videos[0].EtsyVideoId = 102;
        var hashDifferentVideoId = ManagedListingState.Hash(ManagedListingState.From(listingWithVideo).ToCanonicalJson());
        Assert.NotEqual(hashWithVideo, hashDifferentVideoId);

        // Reset
        listingWithVideo.Videos[0].EtsyVideoId = 101;

        // 2. Changing VideoState changes hash (drift flagged)
        listingWithVideo.Videos[0].VideoState = "inactive";
        var hashDifferentState = ManagedListingState.Hash(ManagedListingState.From(listingWithVideo).ToCanonicalJson());
        Assert.NotEqual(hashWithVideo, hashDifferentState);

        // 3. Changing Width/Height changes hash
        listingWithVideo.Videos[0].VideoState = "active";
        listingWithVideo.Videos[0].Width = 1280;
        listingWithVideo.Videos[0].Height = 720;
        var hashDifferentDimensions = ManagedListingState.Hash(ManagedListingState.From(listingWithVideo).ToCanonicalJson());
        Assert.NotEqual(hashWithVideo, hashDifferentDimensions);

        // 4. Non-managed fields (SourceUrl, FileName, ByteSize, DownloadError) do NOT change hash
        listingWithVideo.Videos[0].Width = 1920;
        listingWithVideo.Videos[0].Height = 1080;
        listingWithVideo.Videos[0].SourceUrl = "https://different-source.com/v.mp4";
        listingWithVideo.Videos[0].FileName = "different_name.mp4";
        listingWithVideo.Videos[0].ByteSize = 9999999;
        listingWithVideo.Videos[0].DownloadError = "Network error";
        var hashWithNonManagedChanges = ManagedListingState.Hash(ManagedListingState.From(listingWithVideo).ToCanonicalJson());
        Assert.Equal(hashWithVideo, hashWithNonManagedChanges);
    }

    [Fact]
    public void Video_with_state_deleted_is_filtered_out()
    {
        var baseListing = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos = [],
        };
        var hashNoVideo = ManagedListingState.Hash(ManagedListingState.From(baseListing).ToCanonicalJson());

        // Listing with a deleted video
        var listingWithDeleted = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos =
            [
                new ListingVideo
                {
                    EtsyVideoId = 202,
                    VideoState = "deleted",
                    Width = 1920,
                    Height = 1080,
                }
            ],
        };

        var state = ManagedListingState.From(listingWithDeleted);
        Assert.Null(state.Video);
        var hashDeleted = ManagedListingState.Hash(state.ToCanonicalJson());
        Assert.Equal(hashNoVideo, hashDeleted);

        // Listing with multiple videos: one deleted, one active
        var listingMultiple = new Listing
        {
            EtsyListingId = 1,
            Title = "Vase",
            Description = "Lovely vase",
            PriceAmount = 3000,
            PriceDivisor = 100,
            State = "active",
            Videos =
            [
                new ListingVideo { EtsyVideoId = 202, VideoState = "deleted" },
                new ListingVideo { EtsyVideoId = 303, VideoState = "active", Width = 1080, Height = 1920 },
            ],
        };

        var stateMultiple = ManagedListingState.From(listingMultiple);
        Assert.NotNull(stateMultiple.Video);
        Assert.Equal(303, stateMultiple.Video.EtsyVideoId);
        Assert.Equal("active", stateMultiple.Video.VideoState);
    }

    [Fact]
    public void StorageOptions_VideosDirectory_is_configured_and_created()
    {
        using var temp = new TempStorage();
        var videosDir = temp.Options.VideosDirectory;
        Assert.Equal(Path.Combine(temp.Options.ResolvedRoot, "videos"), videosDir);
        Assert.True(Directory.Exists(videosDir), $"Expected directory {videosDir} to exist.");
    }
}
