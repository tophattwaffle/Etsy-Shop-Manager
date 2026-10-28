using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ChallengerM6VideoStoreStressTests
{
    [Fact]
    public async Task VideoStore_stress_test_multiple_uploads_and_sharding()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var savedFiles = new Dictionary<string, byte[]>();

        // Upload 25 distinct videos
        for (var i = 0; i < 25; i++)
        {
            var content = System.Text.Encoding.UTF8.GetBytes($"video-payload-content-{i}-{Guid.NewGuid()}");
            using var stream = new MemoryStream(content);
            var ext = i % 2 == 0 ? ".mp4" : ".mov";
            var fileName = await store.SaveAsync(stream, ext);

            Assert.True(VideoStore.IsValidFileName(fileName));
            Assert.True(store.Exists(fileName));
            Assert.EndsWith(i % 2 == 0 ? ".mp4" : ".mov", fileName);

            // Sharding folder must be first 2 hex characters of file name
            var shardingFolder = fileName[..2];
            var expectedPath = Path.Combine(temp.Options.VideosDirectory, shardingFolder, fileName);
            Assert.Equal(expectedPath, store.PathFor(fileName));
            Assert.True(File.Exists(expectedPath));

            var readBack = await File.ReadAllBytesAsync(expectedPath);
            Assert.Equal(content, readBack);

            savedFiles[fileName] = content;
        }

        // Verify all 25 distinct files are preserved on disk
        Assert.Equal(25, savedFiles.Count);
        foreach (var (fileName, expectedBytes) in savedFiles)
        {
            Assert.True(store.Exists(fileName));
            var readBack = await File.ReadAllBytesAsync(store.PathFor(fileName));
            Assert.Equal(expectedBytes, readBack);
        }
    }

    [Fact]
    public async Task VideoStore_deduplication_identical_content_creates_single_file()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var identicalBytes = System.Text.Encoding.UTF8.GetBytes("identical-video-stream-repeat-test-12345");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(identicalBytes));
        var expectedFileName = $"{expectedHash}.mp4";

        // Save multiple times via Stream and byte[]
        using (var s1 = new MemoryStream(identicalBytes))
        {
            var f1 = store.Save(s1, "mp4");
            Assert.Equal(expectedFileName, f1);
        }

        using (var s2 = new MemoryStream(identicalBytes))
        {
            var f2 = await store.SaveAsync(s2, "mp4");
            Assert.Equal(expectedFileName, f2);
        }

        var f3 = await store.SaveAsync(identicalBytes, "mp4");
        Assert.Equal(expectedFileName, f3);

        // Verify only 1 file exists in the directory
        var allFiles = Directory.GetFiles(temp.Options.VideosDirectory, "*", SearchOption.AllDirectories);
        Assert.Single(allFiles);
        Assert.Equal(expectedFileName, Path.GetFileName(allFiles[0]));
        Assert.Equal(identicalBytes.Length, new FileInfo(allFiles[0]).Length);
    }

    [Theory]
    [InlineData("../evil.mp4")]
    [InlineData("..\\evil.mp4")]
    [InlineData("../../etc/passwd.mp4")]
    [InlineData("..\\..\\Windows\\System32\\calc.exe")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/shadow")]
    [InlineData("sub/folder/file.mp4")]
    [InlineData("sub\\folder\\file.mp4")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void VideoStore_path_traversal_attempts_are_strictly_rejected(string? maliciousName)
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        Assert.False(VideoStore.IsValidFileName(maliciousName));
        Assert.False(store.Exists(maliciousName));
        Assert.Null(VideoStore.UrlFor(maliciousName));

        if (maliciousName is not null)
        {
            var ex = Assert.Throws<ArgumentException>(() => store.PathFor(maliciousName));
            Assert.Contains("Invalid video file name", ex.Message);
        }
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".jpg")]
    [InlineData(".bat")]
    [InlineData(".sh")]
    [InlineData(".png")]
    [InlineData(".mp3")]
    [InlineData("")]
    public void VideoStore_extension_normalization_falls_back_to_mp4(string ext)
    {
        Assert.Equal("mp4", VideoStore.NormalizeExtension(ext));
    }

    [Theory]
    [InlineData(".mov", "mov")]
    [InlineData("mov", "mov")]
    [InlineData("quicktime", "mov")]
    [InlineData(".quicktime", "mov")]
    [InlineData("MOV", "mov")]
    [InlineData(".MOV", "mov")]
    [InlineData(".mp4", "mp4")]
    [InlineData("mp4", "mp4")]
    [InlineData("MP4", "mp4")]
    public void VideoStore_extension_normalization_maps_mov_and_mp4(string ext, string expected)
    {
        Assert.Equal(expected, VideoStore.NormalizeExtension(ext));
    }

    [Fact]
    public void VideoStore_IsValidFileName_strictly_enforces_sha256_format_and_extension()
    {
        var validHash = new string('a', 64);
        Assert.True(VideoStore.IsValidFileName($"{validHash}.mp4"));
        Assert.True(VideoStore.IsValidFileName($"{validHash}.mov"));

        // Uppercase hex should fail regex [0-9a-f]
        var uppercaseHash = new string('A', 64);
        Assert.False(VideoStore.IsValidFileName($"{uppercaseHash}.mp4"));

        // Uppercase extension should fail
        Assert.False(VideoStore.IsValidFileName($"{validHash}.MP4"));
        Assert.False(VideoStore.IsValidFileName($"{validHash}.MOV"));

        // Wrong length hash
        Assert.False(VideoStore.IsValidFileName($"{new string('a', 63)}.mp4"));
        Assert.False(VideoStore.IsValidFileName($"{new string('a', 65)}.mp4"));

        // Disallowed extensions
        Assert.False(VideoStore.IsValidFileName($"{validHash}.exe"));
        Assert.False(VideoStore.IsValidFileName($"{validHash}.jpg"));
        Assert.False(VideoStore.IsValidFileName($"{validHash}.bat"));
        Assert.False(VideoStore.IsValidFileName($"{validHash}."));
        Assert.False(VideoStore.IsValidFileName(validHash));
    }

    [Fact]
    public async Task VideoStore_cleanup_removes_unreferenced_files_and_preserves_active()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var active1 = await store.SaveAsync("active-1"u8.ToArray(), ".mp4");
        var active2 = await store.SaveAsync("active-2"u8.ToArray(), ".mov");

        var unreferencedOld1 = await store.SaveAsync("unref-old-1"u8.ToArray(), ".mp4");
        var unreferencedOld2 = await store.SaveAsync("unref-old-2"u8.ToArray(), ".mov");

        var unreferencedRecent = await store.SaveAsync("unref-recent"u8.ToArray(), ".mp4");

        var now = DateTime.UtcNow;

        // Set timestamps
        File.SetLastWriteTimeUtc(store.PathFor(active1), now.AddDays(-10));
        File.SetLastWriteTimeUtc(store.PathFor(active2), now.AddDays(-10));
        File.SetLastWriteTimeUtc(store.PathFor(unreferencedOld1), now.AddDays(-5));
        File.SetLastWriteTimeUtc(store.PathFor(unreferencedOld2), now.AddDays(-5));
        File.SetLastWriteTimeUtc(store.PathFor(unreferencedRecent), now.AddMinutes(-5));

        // Create an old .tmp file and a recent .tmp file
        var oldTmpPath = Path.Combine(temp.Options.VideosDirectory, active1[..2], $"{active1}.oldtmp.tmp");
        File.WriteAllText(oldTmpPath, "temporary");
        File.SetLastWriteTimeUtc(oldTmpPath, now.AddHours(-2));

        var recentTmpPath = Path.Combine(temp.Options.VideosDirectory, active1[..2], $"{active1}.recenttmp.tmp");
        File.WriteAllText(recentTmpPath, "temporary");
        File.SetLastWriteTimeUtc(recentTmpPath, now.AddMinutes(-10));

        // Create an unrecognized non-video file in the directory
        var foreignFilePath = Path.Combine(temp.Options.VideosDirectory, "notes.txt");
        File.WriteAllText(foreignFilePath, "some notes");
        File.SetLastWriteTimeUtc(foreignFilePath, now.AddDays(-30));

        var referenced = new HashSet<string> { active1, active2 };
        var minAge = TimeSpan.FromHours(1);

        var deletedCount = store.DeleteUnreferenced(referenced, minAge, now);

        // Exactly 2 unreferenced video files deleted + 1 old tmp file deleted (tmp files don't increment deleted return count)
        Assert.Equal(2, deletedCount);

        // Active files must still exist
        Assert.True(store.Exists(active1));
        Assert.True(store.Exists(active2));

        // Unreferenced old files must be deleted
        Assert.False(store.Exists(unreferencedOld1));
        Assert.False(store.Exists(unreferencedOld2));

        // Unreferenced recent file (< minAge) must still exist
        Assert.True(store.Exists(unreferencedRecent));

        // Old temp file deleted, recent temp file kept
        Assert.False(File.Exists(oldTmpPath));
        Assert.True(File.Exists(recentTmpPath));

        // Foreign file not matching IsValidFileName must NOT be deleted by video cleanup
        Assert.True(File.Exists(foreignFilePath));
    }

    [Fact]
    public async Task FakeEtsyShop_video_mock_endpoints_POST_multipart_parsing_and_journal()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Oak Bookshelf");
        shop.Listings.Add(listingJson);

        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();

        var videoBytes = "video binary mp4 bytes"u8.ToArray();

        // 1. Test POST /v3/application/shops/{shopId}/listings/{listingId}/videos
        var uploadedVideo = await client.UploadListingVideoAsync(42, 1, videoBytes, "showcase.mp4", auth);

        Assert.NotNull(uploadedVideo);
        Assert.True(uploadedVideo.VideoId > 0);
        Assert.Equal("active", uploadedVideo.VideoState);
        Assert.Contains(uploadedVideo.VideoId.ToString(), uploadedVideo.VideoUrl);
        Assert.Contains(uploadedVideo.VideoId.ToString(), uploadedVideo.ThumbnailUrl);

        // Verify FakeEtsyShop journal recorded multipart post
        var write = Assert.Single(shop.Writes, w => w.Method == "POST" && w.Path.Contains("/videos"));
        Assert.Equal("/v3/application/shops/42/listings/1/videos", write.Path);
        Assert.Contains("video=<file showcase.mp4, 22 bytes>", write.Body);

        // Verify listing state in FakeEtsyShop
        var listing = shop.ListingJson(1);
        var videos = listing["videos"]?.AsArray();
        Assert.NotNull(videos);
        Assert.Single(videos);
        Assert.Equal(uploadedVideo.VideoId, (long)videos[0]!["video_id"]!);

        // 2. Test GET /v3/application/listings/{listingId}/videos
        var fetchedVideos = await client.GetListingVideosAsync(1, auth);
        Assert.Single(fetchedVideos);
        Assert.Equal(uploadedVideo.VideoId, fetchedVideos[0].VideoId);

        // 3. Test DELETE /v3/application/shops/{shopId}/listings/{listingId}/videos/{videoId}
        await client.DeleteListingVideoAsync(42, 1, uploadedVideo.VideoId, auth);

        // Verify journal recorded DELETE
        var deleteWrite = Assert.Single(shop.Writes, w => w.Method == "DELETE" && w.Path.Contains("/videos"));
        Assert.Equal($"/v3/application/shops/42/listings/1/videos/{uploadedVideo.VideoId}", deleteWrite.Path);

        // Verify video was removed from listing
        var remainingVideos = await client.GetListingVideosAsync(1, auth);
        Assert.Empty(remainingVideos);

        var listingAfterDelete = shop.ListingJson(1);
        Assert.Empty(listingAfterDelete["videos"]!.AsArray());
    }

    [Fact]
    public async Task FakeEtsyShop_video_mock_endpoints_simulate_failure_modes()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Walnut Table");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9955));
        shop.Listings.Add(listingJson);

        var auth = EtsyAuth.ForShop(FakeEtsyShop.ShopId);
        var client = shop.Provider.GetRequiredService<EtsyApiClient>();

        // 1. Simulate POST failure
        shop.FailWritesWith = HttpStatusCode.GatewayTimeout;
        shop.FailWritesBody = "{\"error\":\"Etsy video processing timed out\"}";

        var exPost = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.UploadListingVideoAsync(42, 1, "test"u8.ToArray(), "vid.mp4", auth));
        Assert.Equal(HttpStatusCode.GatewayTimeout, exPost.StatusCode);

        // 2. Simulate DELETE failure
        shop.FailWritesWith = HttpStatusCode.ServiceUnavailable;
        var exDelete = await Assert.ThrowsAsync<EtsyApiException>(() =>
            client.DeleteListingVideoAsync(42, 1, 9955, auth));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exDelete.StatusCode);
    }

    [Fact]
    public void VideoStore_UrlFor_and_ContentTypeFor_mappings()
    {
        var hash = new string('e', 64);
        var mp4File = $"{hash}.mp4";
        var movFile = $"{hash}.mov";

        Assert.Equal($"media/videos/{mp4File}", VideoStore.UrlFor(mp4File));
        Assert.Equal($"media/videos/{movFile}", VideoStore.UrlFor(movFile));

        Assert.Equal("video/mp4", VideoStore.ContentTypeFor(mp4File));
        Assert.Equal("video/quicktime", VideoStore.ContentTypeFor(movFile));
        Assert.Equal("application/octet-stream", VideoStore.ContentTypeFor("unknown.bin"));
    }
}
