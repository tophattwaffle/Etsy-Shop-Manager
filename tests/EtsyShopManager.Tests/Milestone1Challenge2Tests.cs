using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EtsyShopManager.Tests;

public class Milestone1Challenge2Tests
{
    [Fact]
    public void StorageOptions_VideosDirectory_ResolvesCorrectly_WhenDataRootIsNull()
    {
        var options = new StorageOptions { DataRoot = null };
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EtsyShopManager");
        var expectedVideos = Path.Combine(expectedRoot, "videos");

        Assert.Equal(expectedRoot, options.ResolvedRoot);
        Assert.Equal(expectedVideos, options.VideosDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StorageOptions_VideosDirectory_ResolvesDefault_WhenDataRootIsWhitespace(string whitespaceRoot)
    {
        var options = new StorageOptions { DataRoot = whitespaceRoot };
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EtsyShopManager");
        var expectedVideos = Path.Combine(expectedRoot, "videos");

        Assert.Equal(expectedRoot, options.ResolvedRoot);
        Assert.Equal(expectedVideos, options.VideosDirectory);
    }

    [Fact]
    public void StorageOptions_VideosDirectory_ResolvesCorrectly_WhenDataRootIsCustomPath()
    {
        var custom = Path.Combine(Path.GetTempPath(), "custom-esm-dir");
        var options = new StorageOptions { DataRoot = custom };

        Assert.Equal(Path.GetFullPath(custom), options.ResolvedRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(custom), "videos"), options.VideosDirectory);
    }

    [Fact]
    public void StorageOptions_VideosDirectory_ExpandsEnvironmentVariables()
    {
        var options = new StorageOptions { DataRoot = "%TEMP%\\esm-test-env" };
        var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables("%TEMP%\\esm-test-env"));

        Assert.Equal(expanded, options.ResolvedRoot);
        Assert.Equal(Path.Combine(expanded, "videos"), options.VideosDirectory);
    }

    [Fact]
    public void StorageOptions_EnsureDirectories_CreatesAllSixDirectories_AndIsIdempotent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "esm-test-" + Guid.NewGuid().ToString("N"));
        var options = new StorageOptions { DataRoot = tempRoot };

        try
        {
            Assert.False(Directory.Exists(options.VideosDirectory));
            Assert.False(Directory.Exists(options.ImagesDirectory));
            Assert.False(Directory.Exists(options.BackupsDirectory));
            Assert.False(Directory.Exists(options.KeysDirectory));
            Assert.False(Directory.Exists(options.LogsDirectory));

            options.EnsureDirectories();

            Assert.True(Directory.Exists(options.ResolvedRoot), "ResolvedRoot was not created");
            Assert.True(Directory.Exists(options.ImagesDirectory), "ImagesDirectory was not created");
            Assert.True(Directory.Exists(options.VideosDirectory), "VideosDirectory was not created");
            Assert.True(Directory.Exists(options.KeysDirectory), "KeysDirectory was not created");
            Assert.True(Directory.Exists(options.BackupsDirectory), "BackupsDirectory was not created");
            Assert.True(Directory.Exists(options.LogsDirectory), "LogsDirectory was not created");

            // Ensure idempotency: second call succeeds without error
            options.EnsureDirectories();
            Assert.True(Directory.Exists(options.VideosDirectory));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void StorageOptions_Validate_RejectsDropboxAndOneDrive_AcceptsLocal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new StorageOptions { DataRoot = @"C:\Users\tophattwaffle\Dropbox\EtsyShopManager" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StorageOptions { DataRoot = @"C:\Users\tophattwaffle\OneDrive\EtsyShopManager" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new StorageOptions { DataRoot = @"C:\Users\tophattwaffle\OneDrive - Company\EtsyShopManager" }.Validate());

        new StorageOptions { DataRoot = Path.GetTempPath() }.Validate();
    }

    [Fact]
    public async Task Listing_SkuPattern_PersistsAndLoadsFromDatabase()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1001, Name = "Test Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 5001,
                Title = "Listing with SKU Pattern",
                State = "active",
                SkuPattern = "{listing.sku}-{size}-{color}",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var loaded = await db.Listings.SingleAsync(l => l.EtsyListingId == 5001);
            Assert.Equal("{listing.sku}-{size}-{color}", loaded.SkuPattern);

            // Update to null
            loaded.SkuPattern = null;
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var loaded = await db.Listings.SingleAsync(l => l.EtsyListingId == 5001);
            Assert.Null(loaded.SkuPattern);
        }
    }

    [Fact]
    public async Task Listing_SkuPattern_HandlesMaxLengthOf200Characters()
    {
        using var testDb = new TestDb();
        var pattern = new string('k', 200);

        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1002, Name = "Pattern Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 5002,
                Title = "Pattern Max Length",
                State = "active",
                SkuPattern = pattern,
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var loaded = await db.Listings.SingleAsync(l => l.EtsyListingId == 5002);
            Assert.NotNull(loaded.SkuPattern);
            Assert.Equal(pattern, loaded.SkuPattern);
            Assert.Equal(200, loaded.SkuPattern.Length);
        }
    }

    [Fact]
    public async Task ListingVideo_RoundTrip_PersistsAllFieldsAndUtcKind()
    {
        using var testDb = new TestDb();
        var timestamp = new DateTime(2026, 10, 21, 16, 45, 0, DateTimeKind.Utc);

        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1003, Name = "Video Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 5003,
                Title = "Listing with Video",
                State = "active",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();

            var video = new ListingVideo
            {
                ListingId = listing.Id,
                EtsyVideoId = 9876543210L,
                Height = 1080,
                Width = 1920,
                VideoState = "active",
                SourceUrl = "https://example.com/etsy-video.mp4",
                SourceThumbUrl = "https://example.com/etsy-thumb.jpg",
                FileName = "vid_abc123.mp4",
                ThumbFileName = "thumb_abc123.jpg",
                ByteSize = 10_485_760L,
                DownloadedAtUtc = timestamp,
                DownloadError = null,
            };
            db.ListingVideos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var loaded = await db.ListingVideos
                .Include(v => v.Listing)
                .SingleAsync(v => v.EtsyVideoId == 9876543210L);

            Assert.Equal(5003, loaded.Listing.EtsyListingId);
            Assert.Equal(1080, loaded.Height);
            Assert.Equal(1920, loaded.Width);
            Assert.Equal("active", loaded.VideoState);
            Assert.Equal("https://example.com/etsy-video.mp4", loaded.SourceUrl);
            Assert.Equal("https://example.com/etsy-thumb.jpg", loaded.SourceThumbUrl);
            Assert.Equal("vid_abc123.mp4", loaded.FileName);
            Assert.Equal("thumb_abc123.jpg", loaded.ThumbFileName);
            Assert.Equal(10_485_760L, loaded.ByteSize);
            Assert.Equal(timestamp, loaded.DownloadedAtUtc);
            Assert.Equal(DateTimeKind.Utc, loaded.DownloadedAtUtc!.Value.Kind);
            Assert.Null(loaded.DownloadError);
        }
    }

    [Fact]
    public async Task ListingVideo_MinimalFields_AllowsNullOptionalsAndSavesSuccessfully()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1004, Name = "Minimal Video Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 5004,
                Title = "Minimal Video",
                State = "active",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();

            var video = new ListingVideo
            {
                ListingId = listing.Id,
                EtsyVideoId = 112233L,
                SourceUrl = "https://example.com/video.mp4",
            };
            db.ListingVideos.Add(video);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var loaded = await db.ListingVideos.SingleAsync(v => v.EtsyVideoId == 112233L);
            Assert.Null(loaded.Height);
            Assert.Null(loaded.Width);
            Assert.Null(loaded.VideoState);
            Assert.Null(loaded.SourceThumbUrl);
            Assert.Null(loaded.FileName);
            Assert.Null(loaded.ThumbFileName);
            Assert.Null(loaded.ByteSize);
            Assert.Null(loaded.DownloadedAtUtc);
            Assert.Null(loaded.DownloadError);
            Assert.Equal("https://example.com/video.mp4", loaded.SourceUrl);
        }
    }

    [Fact]
    public async Task ListingVideo_CompositeUniqueConstraint_AllowsSameEtsyVideoIdOnDifferentListings()
    {
        using var testDb = new TestDb();
        await using var db = testDb.CreateDbContext();

        var shop = new Shop { EtsyShopId = 1005, Name = "Shared Video Shop", CurrencyCode = "USD" };
        db.Shops.Add(shop);
        await db.SaveChangesAsync();

        var listing1 = new Listing { ShopId = shop.Id, EtsyListingId = 6001, Title = "Listing 1", State = "active" };
        var listing2 = new Listing { ShopId = shop.Id, EtsyListingId = 6002, Title = "Listing 2", State = "active" };
        db.Listings.AddRange(listing1, listing2);
        await db.SaveChangesAsync();

        // Same EtsyVideoId on two different listings is allowed (composite key: {ListingId, EtsyVideoId})
        db.ListingVideos.Add(new ListingVideo { ListingId = listing1.Id, EtsyVideoId = 99999L, SourceUrl = "https://example.com/v.mp4" });
        db.ListingVideos.Add(new ListingVideo { ListingId = listing2.Id, EtsyVideoId = 99999L, SourceUrl = "https://example.com/v.mp4" });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ListingVideos.CountAsync(v => v.EtsyVideoId == 99999L));

        // Duplicate on same listing MUST throw DbUpdateException
        db.ListingVideos.Add(new ListingVideo { ListingId = listing1.Id, EtsyVideoId = 99999L, SourceUrl = "https://example.com/duplicate.mp4" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ListingVideo_CascadeDelete_WhenListingIsDeleted()
    {
        using var testDb = new TestDb();
        int listingId;

        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1006, Name = "Cascade Shop", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 5006,
                Title = "Listing to Delete",
                State = "active",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
            listingId = listing.Id;

            db.ListingVideos.AddRange(
                new ListingVideo { ListingId = listingId, EtsyVideoId = 101, SourceUrl = "https://example.com/1.mp4" },
                new ListingVideo { ListingId = listingId, EtsyVideoId = 102, SourceUrl = "https://example.com/2.mp4" }
            );
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            Assert.Equal(2, await db.ListingVideos.CountAsync(v => v.ListingId == listingId));

            var listing = await db.Listings.FindAsync(listingId);
            Assert.NotNull(listing);
            db.Listings.Remove(listing);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            Assert.Equal(0, await db.Listings.CountAsync(l => l.Id == listingId));
            Assert.Equal(0, await db.ListingVideos.CountAsync(v => v.ListingId == listingId));
        }
    }

    [Fact]
    public async Task ListingVideo_CascadeDelete_WhenShopIsDeleted()
    {
        using var testDb = new TestDb();
        int shopId;
        int listingId;

        await using (var db = testDb.CreateDbContext())
        {
            var shop = new Shop { EtsyShopId = 1007, Name = "Shop to Delete", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();
            shopId = shop.Id;

            var listing = new Listing
            {
                ShopId = shopId,
                EtsyListingId = 5007,
                Title = "Listing under Shop",
                State = "active",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
            listingId = listing.Id;

            db.ListingVideos.Add(new ListingVideo
            {
                ListingId = listingId,
                EtsyVideoId = 201,
                SourceUrl = "https://example.com/v.mp4"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            var shop = await db.Shops.FindAsync(shopId);
            Assert.NotNull(shop);
            db.Shops.Remove(shop);
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            Assert.Equal(0, await db.Shops.CountAsync(s => s.Id == shopId));
            Assert.Equal(0, await db.Listings.CountAsync(l => l.Id == listingId));
            Assert.Equal(0, await db.ListingVideos.CountAsync(v => v.ListingId == listingId));
        }
    }

    [Fact]
    public void ManagedListingState_V6_HandlesVideosAndNullDefaults()
    {
        Assert.Equal(6, ManagedListingState.CurrentVersion);

        var listing = new Listing
        {
            Title = "Vase",
            State = "active",
            PriceAmount = 2000,
            PriceDivisor = 100,
        };

        // 1. Empty videos -> null
        var state1 = ManagedListingState.From(listing);
        Assert.Null(state1.Video);

        // 2. Video with VideoState = null defaults to "active"
        listing.Videos.Add(new ListingVideo
        {
            EtsyVideoId = 555L,
            VideoState = null,
            Width = 1080,
            Height = 1920,
            SourceUrl = "https://example.com/video.mp4"
        });
        var state2 = ManagedListingState.From(listing);
        Assert.NotNull(state2.Video);
        Assert.Equal("active", state2.Video.VideoState);
        Assert.Equal(555L, state2.Video.EtsyVideoId);

        // 3. SkuPattern does not alter ManagedListingState hash
        var hashBeforeSku = ManagedListingState.Hash(state2.ToCanonicalJson());
        listing.SkuPattern = "{listing.sku}-{size}";
        var hashAfterSku = ManagedListingState.Hash(ManagedListingState.From(listing).ToCanonicalJson());
        Assert.Equal(hashBeforeSku, hashAfterSku);
    }
}
