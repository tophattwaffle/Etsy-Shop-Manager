using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Milestone 8 (Tier 5): Adversarial Coverage Hardening for R5 (Listing Videos) and R6 (Template Listing Filters).
/// Stress-tests edge cases across VideoStore, video streaming range requests, video editor diff/blockers,
/// push delete-before-upload ordering, cloner video replication, TemplateFilterMatcher diacritics/regex/overflow,
/// and single/composite tri-state scoped bulk selection harnesses.
/// </summary>
public class AdversarialTier5_R5_R6_Tests
{
    #region Helpers & Test Models

    private sealed record MockTemplateItem : ITemplateListingItem
    {
        public int ListingId { get; init; } = 1;
        public string Title { get; init; } = "";
        public int ShopId { get; init; } = 1;
        public string ShopName { get; init; } = "Shop One";
        public long EtsyListingId { get; init; } = 123456789;
        public string State { get; init; } = "active";
        public bool IsMissingOnEtsy { get; init; }
        public IReadOnlyList<string> Skus { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
        public long PropertyId { get; init; } = 0;
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _index;

        public ForwardOnlyStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Non-seekable forward-only stream.");
        public override long Position
        {
            get => throw new NotSupportedException("Non-seekable forward-only stream.");
            set => throw new NotSupportedException("Non-seekable forward-only stream.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_index >= _data.Length) return 0;
            var toRead = Math.Min(count, _data.Length - _index);
            Array.Copy(_data, _index, buffer, offset, toRead);
            _index += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SingleKeySelectionHarness
    {
        public List<MockTemplateItem> AllItems { get; } = [];
        public TemplateListingFilter Filter { get; set; } = new();
        public HashSet<int> Picked { get; } = [];

        public IReadOnlyList<MockTemplateItem> Visible =>
            [.. AllItems.Where(i => TemplateFilterMatcher.Matches(i, Filter))];

        public bool? CandidateHeaderChecked
        {
            get
            {
                var visible = Visible.Select(c => c.ListingId).ToList();
                if (visible.Count == 0) return false;
                var pickedCount = visible.Count(Picked.Contains);
                if (pickedCount == 0) return false;
                if (pickedCount == visible.Count) return true;
                return null;
            }
        }

        public void TogglePickAllVisible()
        {
            var visible = Visible.Select(c => c.ListingId).ToList();
            if (visible.Count == 0) return;
            var allPicked = visible.All(Picked.Contains);
            if (allPicked)
                Picked.ExceptWith(visible);
            else
                Picked.UnionWith(visible);
        }

        public void Pick(int listingId, bool on)
        {
            if (on) Picked.Add(listingId); else Picked.Remove(listingId);
        }
    }

    private sealed class CompositeKeySelectionHarness
    {
        public List<MockTemplateItem> AllItems { get; } = [];
        public TemplateListingFilter Filter { get; set; } = new();
        public HashSet<(int ListingId, long PropertyId)> Picked { get; } = [];

        public IReadOnlyList<MockTemplateItem> Visible =>
            [.. AllItems.Where(i => TemplateFilterMatcher.Matches(i, Filter))];

        public bool? CandidateHeaderChecked
        {
            get
            {
                var visible = Visible.Select(c => (c.ListingId, c.PropertyId)).ToList();
                if (visible.Count == 0) return false;
                var pickedCount = visible.Count(Picked.Contains);
                if (pickedCount == 0) return false;
                if (pickedCount == visible.Count) return true;
                return null;
            }
        }

        public void TogglePickAllVisible()
        {
            var visible = Visible.Select(c => (c.ListingId, c.PropertyId)).ToList();
            if (visible.Count == 0) return;
            var allPicked = visible.All(Picked.Contains);
            if (allPicked)
                Picked.ExceptWith(visible);
            else
                Picked.UnionWith(visible);
        }

        public void Pick(int listingId, long propertyId, bool on)
        {
            if (on) Picked.Add((listingId, propertyId)); else Picked.Remove((listingId, propertyId));
        }
    }

    private static IResult HandleVideoEndpointRequest(string fileName, VideoStore store, HttpContext http)
    {
        if (!store.Exists(fileName)) return Results.NotFound();
        http.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        return Results.File(store.PathFor(fileName), VideoStore.ContentTypeFor(fileName), enableRangeProcessing: true);
    }

    #endregion

    #region Section 1: R5 — VideoStore & Streaming Endpoint Adversarial Hardening

    [Theory]
    [InlineData("video/quicktime", null, "mov")]
    [InlineData("VIDEO/QUICKTIME", null, "mov")]
    [InlineData("Video/QuickTime", null, "mov")]
    [InlineData("video/mp4", null, "mp4")]
    [InlineData("VIDEO/MP4", null, "mp4")]
    [InlineData("Video/Mp4", null, "mp4")]
    [InlineData("application/octet-stream", "https://img.etsy.com/video_123.MOV?version=1#sec", "mov")]
    [InlineData(null, "https://img.etsy.com/video_123.quicktime", "mov")]
    [InlineData(null, "https://img.etsy.com/video_123.QUICKTIME", "mov")]
    [InlineData(null, "https://img.etsy.com/video_123.Mp4", "mp4")]
    [InlineData(null, "https://img.etsy.com/video_123.MP4", "mp4")]
    [InlineData(null, "https://img.etsy.com/video_123", "mp4")]
    [InlineData(null, null, "mp4")]
    [InlineData("", "", "mp4")]
    public void VideoStore_ExtensionParsing_CaseSensitivity_And_WeirdFormats(string? contentType, string? url, string expectedExt)
    {
        var resolved = VideoStore.ExtensionFor(contentType, url);
        Assert.Equal(expectedExt, resolved);
    }

    [Theory]
    [InlineData(".MOV", "mov")]
    [InlineData("MOV", "mov")]
    [InlineData(".mov", "mov")]
    [InlineData("mov", "mov")]
    [InlineData(".quicktime", "mov")]
    [InlineData("quicktime", "mov")]
    [InlineData("QUICKTIME", "mov")]
    [InlineData(".QUICKTIME", "mov")]
    [InlineData(".mp4", "mp4")]
    [InlineData("mp4", "mp4")]
    [InlineData("MP4", "mp4")]
    [InlineData(".MP4", "mp4")]
    [InlineData(".webm", "mp4")]
    [InlineData(".mkv", "mp4")]
    [InlineData(".avi", "mp4")]
    [InlineData("", "mp4")]
    [InlineData("   ", "mp4")]
    public void VideoStore_NormalizeExtension_MapsCorrectly(string ext, string expected)
    {
        Assert.Equal(expected, VideoStore.NormalizeExtension(ext));
    }

    [Fact]
    public async Task VideoStore_EmptyStream_ZeroBytePayload_Deduplication()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        byte[] emptyBytes = [];
        var emptyHash = Convert.ToHexStringLower(SHA256.HashData(emptyBytes));
        var expectedFileName = $"{emptyHash}.mp4";

        using (var s1 = new MemoryStream(emptyBytes))
        {
            var f1 = store.Save(s1, "mp4");
            Assert.Equal(expectedFileName, f1);
            Assert.True(store.Exists(f1));
        }

        using (var s2 = new MemoryStream(emptyBytes))
        {
            var f2 = await store.SaveAsync(s2, ".mov");
            // Hash is the same, extension is mov
            var expectedMov = $"{emptyHash}.mov";
            Assert.Equal(expectedMov, f2);
            Assert.True(store.Exists(f2));
        }

        // Deduplication check: saving empty bytes again returns existing file
        var f3 = await store.SaveAsync(emptyBytes, "mp4");
        Assert.Equal(expectedFileName, f3);

        var filePath = store.PathFor(expectedFileName);
        Assert.True(File.Exists(filePath));
        Assert.Equal(0, new FileInfo(filePath).Length);
    }

    [Fact]
    public async Task VideoStore_NonSeekable_ForwardOnlyStream_Save()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var content = "unseekable-video-stream-content-999"u8.ToArray();
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(content));

        // Synchronous Save with forward-only stream
        using (var streamSync = new ForwardOnlyStream(content))
        {
            var fileName = store.Save(streamSync, "mp4");
            Assert.Equal($"{expectedHash}.mp4", fileName);
            Assert.True(store.Exists(fileName));
            Assert.Equal(content, await File.ReadAllBytesAsync(store.PathFor(fileName)));
        }

        // Asynchronous SaveAsync with forward-only stream
        using (var streamAsync = new ForwardOnlyStream(content))
        {
            var fileNameAsync = await store.SaveAsync(streamAsync, "mov");
            Assert.Equal($"{expectedHash}.mov", fileNameAsync);
            Assert.True(store.Exists(fileNameAsync));
            Assert.Equal(content, await File.ReadAllBytesAsync(store.PathFor(fileNameAsync)));
        }
    }

    [Fact]
    public async Task VideoStore_HighConcurrency_MultiThreadedStress()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        const int workerCount = 20;
        var tasks = new Task<string>[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i;
            var payload = System.Text.Encoding.UTF8.GetBytes($"distinct-video-payload-{workerId}-{Guid.NewGuid()}");

            tasks[workerId] = Task.Run(async () =>
            {
                using var ms = new MemoryStream(payload);
                return await store.SaveAsync(ms, workerId % 3 == 0 ? "mov" : "mp4");
            });
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(workerCount, results.Length);

        foreach (var file in results)
        {
            Assert.True(VideoStore.IsValidFileName(file));
            Assert.True(store.Exists(file));
            var fullPath = store.PathFor(file);
            Assert.True(File.Exists(fullPath));
            Assert.True(new FileInfo(fullPath).Length > 0);
        }
    }

    [Theory]
    [InlineData("../secret.mp4")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\boot.ini")]
    [InlineData("/root/.ssh/id_rsa")]
    [InlineData("g000000000000000000000000000000000000000000000000000000000000000.mp4")]
    [InlineData("abcdef123456.mp4")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.mp4")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.exe")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.mp4.exe")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.dll")]
    [InlineData("CON")]
    [InlineData("AUX")]
    [InlineData("")]
    [InlineData(null)]
    public void VideoStore_PathTraversal_And_IllegalFileNames_Comprehensive(string? maliciousName)
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        Assert.False(VideoStore.IsValidFileName(maliciousName));
        Assert.False(store.Exists(maliciousName));
        Assert.Null(VideoStore.UrlFor(maliciousName));

        if (maliciousName is not null)
        {
            Assert.Throws<ArgumentException>(() => store.PathFor(maliciousName));
        }
    }

    [Fact]
    public async Task VideoStreamingEndpoint_Adversarial_Range_And_Traversal_Attacks()
    {
        using var shop = new FakeEtsyShop();

        byte[] videoContent = [.. Enumerable.Range(0, 50).Select(i => (byte)i)];
        var validFile = await shop.VideoStore.SaveAsync(videoContent, ".mp4");

        // 1. Boundary single byte range: bytes=0-0
        var httpZero = new DefaultHttpContext { RequestServices = shop.Provider };
        httpZero.Request.Method = "GET";
        var bodyZero = new MemoryStream();
        httpZero.Response.Body = bodyZero;
        httpZero.Request.Headers.Range = "bytes=0-0";

        var resZero = HandleVideoEndpointRequest(validFile, shop.VideoStore, httpZero);
        await resZero.ExecuteAsync(httpZero);
        Assert.Equal((int)HttpStatusCode.PartialContent, httpZero.Response.StatusCode);
        Assert.Equal(1, bodyZero.Length);
        Assert.Equal(videoContent[0], bodyZero.ToArray()[0]);
        Assert.Equal("bytes 0-0/50", httpZero.Response.Headers.ContentRange.ToString());
        Assert.Equal("public, max-age=31536000, immutable", httpZero.Response.Headers.CacheControl.ToString());

        // 2. Traversal attempt on endpoint: returns 404 NotFound
        var httpTraversal = new DefaultHttpContext { RequestServices = shop.Provider };
        httpTraversal.Request.Method = "GET";
        var resTraversal = HandleVideoEndpointRequest("../secret.mp4", shop.VideoStore, httpTraversal);
        await resTraversal.ExecuteAsync(httpTraversal);
        Assert.Equal((int)HttpStatusCode.NotFound, httpTraversal.Response.StatusCode);

        // 3. Nonexistent file on endpoint: returns 404 NotFound
        var nonExistentHex = new string('f', 64) + ".mp4";
        var httpNonExistent = new DefaultHttpContext { RequestServices = shop.Provider };
        var resNonExistent = HandleVideoEndpointRequest(nonExistentHex, shop.VideoStore, httpNonExistent);
        await resNonExistent.ExecuteAsync(httpNonExistent);
        Assert.Equal((int)HttpStatusCode.NotFound, httpNonExistent.Response.StatusCode);
    }

    [Fact]
    public async Task VideoStore_DeleteUnreferenced_EdgeCases()
    {
        using var temp = new TempStorage();
        var store = new VideoStore(temp.Options);

        var now = DateTime.UtcNow;

        var referenced = await store.SaveAsync("ref-video"u8.ToArray(), ".mp4");
        var unrefOld = await store.SaveAsync("unref-old"u8.ToArray(), ".mp4");
        var unrefYoung = await store.SaveAsync("unref-young"u8.ToArray(), ".mp4");

        File.SetLastWriteTimeUtc(store.PathFor(referenced), now.AddDays(-10));
        File.SetLastWriteTimeUtc(store.PathFor(unrefOld), now.AddDays(-10));
        File.SetLastWriteTimeUtc(store.PathFor(unrefYoung), now.AddMinutes(-10));

        // Create an abandoned tmp file (>1h) and a fresh tmp file (<1h)
        var oldTmp = Path.Combine(temp.Options.VideosDirectory, referenced[..2], $"{referenced}.old.tmp");
        var freshTmp = Path.Combine(temp.Options.VideosDirectory, referenced[..2], $"{referenced}.fresh.tmp");
        await File.WriteAllTextAsync(oldTmp, "abandoned");
        await File.WriteAllTextAsync(freshTmp, "fresh");
        File.SetLastWriteTimeUtc(oldTmp, now.AddHours(-2));
        File.SetLastWriteTimeUtc(freshTmp, now.AddMinutes(-5));

        var deleted = store.DeleteUnreferenced(new HashSet<string> { referenced }, TimeSpan.FromDays(1), now);

        Assert.Equal(1, deleted); // unrefOld deleted
        Assert.True(store.Exists(referenced));
        Assert.False(store.Exists(unrefOld));
        Assert.True(store.Exists(unrefYoung));
        Assert.False(File.Exists(oldTmp)); // old tmp deleted
        Assert.True(File.Exists(freshTmp)); // fresh tmp kept
    }

    #endregion

    #region Section 2: R5 — ListingEditor & PushListingJob Adversarial Workflow

    [Fact]
    public async Task ListingEditor_Video_Preview_MissingLocalFile_BlocksPush()
    {
        using var shop = new FakeEtsyShop();

        shop.Listings.Add(FakeEtsyShop.Listing(201, "Pottery Mug"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int localId;
        await using (var db = shop.Db.CreateDbContext())
            localId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 201)).Id;

        var missingFileName = new string('e', 64) + ".mp4";
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = missingFileName,
                ["name"] = "mug_tour.mp4"
            }
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(localId);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The video file was added here but its file is gone"));

        var (pushId, blockers) = await editor.RequestPushAsync(localId, "tophattwaffle");
        Assert.Null(pushId);
        Assert.Contains(blockers, b => b.Contains("The video file was added here but its file is gone"));
    }

    [Fact]
    public async Task ListingEditor_Video_Preview_Warnings_For_Upload_Replace_Delete()
    {
        using var shop = new FakeEtsyShop();

        // 1. Initial listing has no video
        shop.Listings.Add(FakeEtsyShop.Listing(202, "Wooden Bowl"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int localId;
        await using (var db = shop.Db.CreateDbContext())
            localId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 202)).Id;

        var videoFile = await shop.VideoStore.SaveAsync("sample-bytes"u8.ToArray(), ".mp4");
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        // Step A: Add video -> 1 call warning
        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject { ["file_name"] = videoFile, ["name"] = "bowl.mp4" }
        }, "tophattwaffle");

        var prevAdd = await editor.PreviewAsync(localId);
        Assert.True(prevAdd.CanPush);
        Assert.Contains(prevAdd.Warnings, w => w.Contains("Video: 1 call to Etsy (POST"));

        // Step B: Simulate Etsy having existing video #8888
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.Include(x => x.EtsyState).SingleAsync(x => x.Id == localId);
            var managed = JsonNode.Parse(l.EtsyState!.ManagedJson)!.AsObject();
            managed[ListingFields.Video] = new JsonObject
            {
                ["etsy_video_id"] = 8888L,
                ["video_state"] = "active"
            };
            l.EtsyState.ManagedJson = managed.ToJsonString();
            l.EtsyState.BaseJson = managed.ToJsonString();
            await db.SaveChangesAsync();
        }

        // Replace video -> 2 calls warning (DELETE then POST)
        var newFile = await shop.VideoStore.SaveAsync("new-bytes"u8.ToArray(), ".mp4");
        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject { ["file_name"] = newFile, ["name"] = "bowl_v2.mp4" }
        }, "tophattwaffle");

        var prevReplace = await editor.PreviewAsync(localId);
        Assert.True(prevReplace.CanPush);
        Assert.Contains(prevReplace.Warnings, w => w.Contains("Video: 2 calls to Etsy (DELETE existing video #8888, then POST to upload"));

        // Step C: Delete video -> 1 call warning (DELETE)
        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = null
        }, "tophattwaffle");

        var prevDelete = await editor.PreviewAsync(localId);
        Assert.True(prevDelete.CanPush);
        Assert.Contains(prevDelete.Warnings, w => w.Contains("Video: 1 call to Etsy (DELETE /v3/application/shops/42/listings/202/videos/8888) to remove the video."));
    }

    [Fact]
    public void ListingEditor_DescribeVideo_And_DescribeVideoChange_Adversarial()
    {
        Assert.Equal("No video", ListingEditor.DescribeVideo(null));

        var namedNode = new JsonObject { ["name"] = "spin.mp4" };
        Assert.Equal("New video (spin.mp4)", ListingEditor.DescribeVideo(namedNode));

        var fileNode = new JsonObject { ["file_name"] = "hash.mp4" };
        Assert.Equal("New video (hash.mp4)", ListingEditor.DescribeVideo(fileNode));

        var etsyNode = new JsonObject { ["etsy_video_id"] = 12345L, ["video_state"] = "active" };
        Assert.Equal("Etsy video #12345 (active)", ListingEditor.DescribeVideo(etsyNode));

        var etsyNodeNoState = new JsonObject { ["etsy_video_id"] = 12345L };
        Assert.Equal("Etsy video #12345", ListingEditor.DescribeVideo(etsyNodeNoState));

        var emptyNode = new JsonObject();
        Assert.Equal("Video", ListingEditor.DescribeVideo(emptyNode));

        // DescribeVideoChange
        Assert.Equal("No change", ListingEditor.DescribeVideoChange(null, null));
        Assert.Equal("Add video: spin.mp4", ListingEditor.DescribeVideoChange(null, namedNode));
        Assert.Equal("Remove video #12345", ListingEditor.DescribeVideoChange(etsyNode, null));
        Assert.Equal("Replace video #12345 with spin.mp4", ListingEditor.DescribeVideoChange(etsyNode, namedNode));
    }

    [Fact]
    public async Task PushListingJob_Video_Replacement_Sequence_Delete_Before_Upload()
    {
        using var shop = new FakeEtsyShop();

        var listingJson = FakeEtsyShop.Listing(203, "Ceramic Planter");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(203, 9999));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int localId;
        await using (var db = shop.Db.CreateDbContext())
            localId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 203)).Id;

        var newFile = await shop.VideoStore.SaveAsync("planter-spin-bytes"u8.ToArray(), ".mp4");
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = newFile,
                ["name"] = "planter_360.mp4"
            }
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(localId, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options)
        }, CancellationToken.None);

        // Verification: DELETE strictly preceded POST in the recorded API writes
        var videoCalls = shop.Writes.Where(w => w.Path.Contains("/videos")).ToList();
        Assert.Equal(2, videoCalls.Count);

        var deleteCall = videoCalls[0];
        var uploadCall = videoCalls[1];

        Assert.Equal("DELETE", deleteCall.Method);
        Assert.Contains("/videos/9999", deleteCall.Path);

        Assert.Equal("POST", uploadCall.Method);
        Assert.EndsWith("/videos", uploadCall.Path);
        Assert.Contains("planter_360.mp4", uploadCall.Body);
    }

    [Fact]
    public async Task PushListingJob_Video_DeletedVideoRecord_Ignored()
    {
        using var shop = new FakeEtsyShop();

        shop.Listings.Add(FakeEtsyShop.Listing(204, "Copper Lamp"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int localId;
        await using (var db = shop.Db.CreateDbContext())
            localId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 204)).Id;

        // Manually insert an old video record with VideoState = "deleted"
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ListingVideos.Add(new ListingVideo
            {
                ListingId = localId,
                EtsyVideoId = 7777,
                VideoState = "deleted",
                SourceUrl = "https://example.com/old.mp4",
                FileName = "old.mp4"
            });
            await db.SaveChangesAsync();
        }

        var newFile = await shop.VideoStore.SaveAsync("lamp-spin-bytes"u8.ToArray(), ".mp4");
        var editor = shop.Provider.GetRequiredService<ListingEditor>();

        await editor.SaveAsync(localId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = newFile,
                ["name"] = "lamp_spin.mp4"
            }
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(localId, "tophattwaffle");
        Assert.Empty(blockers);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 2,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options)
        }, CancellationToken.None);

        // Assert: Zero DELETE calls made because old video record was already "deleted"
        var deleteCalls = shop.Writes.Where(w => w.Method == "DELETE" && w.Path.Contains("/videos")).ToList();
        Assert.Empty(deleteCalls);

        var uploadCalls = shop.Writes.Where(w => w.Method == "POST" && w.Path.EndsWith("/videos")).ToList();
        Assert.Single(uploadCalls);
    }

    [Fact]
    public async Task ListingCloner_Video_Copying_ExhaustiveMatrix()
    {
        using var shop = new FakeEtsyShop();

        // 1. Source listing has active video
        var listingJson = FakeEtsyShop.Listing(205, "Leather Bag", withImages: false);
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(205, 5555));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int localId;
        await using (var db = shop.Db.CreateDbContext())
            localId = (await db.Listings.SingleAsync(l => l.EtsyListingId == 205)).Id;

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.Where(i => i.ListingId == localId).ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(localId, FakeEtsyShop.ShopId, CancellationToken.None);

        // Case A: Video exists locally -> Plan contains video upload call
        var planA = await cloner.PlanAsync(localId, choice, CancellationToken.None);
        Assert.True(planA.CanClone, string.Join("; ", planA.Blockers));
        Assert.Contains(planA.Calls, c => c.Contains("POST") && c.Contains("/videos"));

        // Case B: Video marked deleted in source -> Cloner does not copy
        await using (var db = shop.Db.CreateDbContext())
        {
            var vid = await db.ListingVideos.SingleAsync(v => v.ListingId == localId);
            vid.VideoState = "deleted";
            await db.SaveChangesAsync();
        }

        var planB = await cloner.PlanAsync(localId, choice, CancellationToken.None);
        Assert.True(planB.CanClone);
        Assert.DoesNotContain(planB.Calls, c => c.Contains("/videos"));

        // Case C: Source local draft removed video -> Cloner does not copy
        await using (var db = shop.Db.CreateDbContext())
        {
            var vid = await db.ListingVideos.SingleAsync(v => v.ListingId == localId);
            vid.VideoState = "active";
            var l = await db.Listings.SingleAsync(x => x.Id == localId);
            l.DraftJson = JsonSerializer.Serialize(new Dictionary<string, JsonNode?>
            {
                [ListingFields.Video] = null
            });
            await db.SaveChangesAsync();
        }

        var planC = await cloner.PlanAsync(localId, choice, CancellationToken.None);
        Assert.True(planC.CanClone);
        Assert.DoesNotContain(planC.Calls, c => c.Contains("/videos"));

        // Case D: Source local draft replaced video with new local file -> Cloner copies new file
        var newFile = await shop.VideoStore.SaveAsync("new-bag-video"u8.ToArray(), ".mp4");
        await using (var db = shop.Db.CreateDbContext())
        {
            var l = await db.Listings.SingleAsync(x => x.Id == localId);
            l.DraftJson = JsonSerializer.Serialize(new Dictionary<string, JsonNode?>
            {
                [ListingFields.Video] = new JsonObject { ["file_name"] = newFile }
            });
            await db.SaveChangesAsync();
        }

        var planD = await cloner.PlanAsync(localId, choice, CancellationToken.None);
        Assert.True(planD.CanClone);
        Assert.Contains(planD.Calls, c => c.Contains("/videos") && c.Contains(newFile));
    }

    #endregion

    #region Section 3: R6 — TemplateFilterMatcher & Search Query Hardening

    [Fact]
    public void TemplateFilterMatcher_Accents_Diacritics_And_Unicode_CaseInsensitive()
    {
        var item = new MockTemplateItem
        {
            Title = "Handmade Café Mug with Crème Glaze",
            Skus = ["MÜNCHEN-01", "SMÖRGÅSBORD-A"],
            Tags = ["café", "crème", "smörgåsbord", "handgemacht"]
        };

        // Matching with exact accent in lowercase, uppercase, mixed case
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "café" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "CAFÉ" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Crème" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "CRÈme" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "münchen" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "MÜNCHEN" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "smörgåsbord" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "SMÖRGÅSBORD" }));

        // Non-Latin Unicode (Japanese Kanji and Cyrillic)
        var nonLatinItem = new MockTemplateItem
        {
            Title = "手作りシルクスカーフ - Silk Scarf",
            Skus = ["JP-СВИТЕР-01"],
            Tags = ["手作り", "свитер"]
        };

        Assert.True(TemplateFilterMatcher.Matches(nonLatinItem, new TemplateListingFilter { Text = "手作り" }));
        Assert.True(TemplateFilterMatcher.Matches(nonLatinItem, new TemplateListingFilter { Text = "シルク" }));
        Assert.True(TemplateFilterMatcher.Matches(nonLatinItem, new TemplateListingFilter { Text = "СВИТЕР" }));
        Assert.True(TemplateFilterMatcher.Matches(nonLatinItem, new TemplateListingFilter { Text = "свитер" }));
    }

    [Theory]
    [InlineData(".*")]
    [InlineData("^pattern$")]
    [InlineData("[0-9]+")]
    [InlineData("(a|b)")]
    [InlineData(@"\d+")]
    [InlineData("?+*{}()|")]
    [InlineData("$19.99")]
    [InlineData("100%")]
    [InlineData("item#01")]
    [InlineData("tag/v2")]
    public void TemplateFilterMatcher_SpecialCharacters_And_RegexSyntax_LiteralMatch(string specialPattern)
    {
        var matchingItem = new MockTemplateItem
        {
            Title = $"Product with {specialPattern} in title",
            Skus = [$"SKU-{specialPattern}"],
            Tags = [$"tag-{specialPattern}"]
        };

        var plainItem = new MockTemplateItem
        {
            Title = "Standard plain title",
            Skus = ["PLAIN-01"],
            Tags = ["plain"]
        };

        var filter = new TemplateListingFilter { Text = specialPattern };

        Assert.True(TemplateFilterMatcher.Matches(matchingItem, filter));
        Assert.False(TemplateFilterMatcher.Matches(plainItem, filter));
    }

    [Fact]
    public void TemplateFilterMatcher_EtsyListingId_LeadingZeros_And_Int64Overflow()
    {
        var item = new MockTemplateItem
        {
            EtsyListingId = 9876543210L,
            Title = "Vintage Brass Compass",
            Skus = ["COMPASS-01"]
        };

        // Leading zeros
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "0009876543210" }));

        // Leading and trailing whitespace around ID
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "   9876543210   " }));

        // Massive numeric string exceeding long.MaxValue does not throw OverflowException
        const string massiveOverflow = "99999999999999999999999999999999999999999999999999";
        var overflowFilter = new TemplateListingFilter { Text = massiveOverflow };

        // Should return false cleanly without crashing
        Assert.False(TemplateFilterMatcher.Matches(item, overflowFilter));

        // If title contains that massive numeric string, it matches via text substring!
        var itemWithMassiveNumber = new MockTemplateItem
        {
            Title = $"Item with massive ID {massiveOverflow} in title"
        };
        Assert.True(TemplateFilterMatcher.Matches(itemWithMassiveNumber, overflowFilter));
    }

    [Fact]
    public void TemplateFilterMatcher_NullAndCorrupted_Fields_Robustness()
    {
        var corruptedItem = new MockTemplateItem
        {
            Title = null!,
            Skus = [null!, "", "VALID-SKU"],
            Tags = [null!, "  ", "valid-tag"]
        };

        // Null search text
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = null }));

        // Null status (defaults to "all")
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Status = null! }));

        // Search text matching valid sku amidst null elements
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = "VALID-SKU" }));

        // Search text matching valid tag amidst null elements
        Assert.True(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = "valid-tag" }));

        // Search text matching nothing
        Assert.False(TemplateFilterMatcher.Matches(corruptedItem, new TemplateListingFilter { Text = "non-existent" }));

        // Item with null Skus and null Tags collections
        var nullCollectionsItem = new MockTemplateItem
        {
            Title = "Only Title",
            Skus = null!,
            Tags = null!
        };

        Assert.True(TemplateFilterMatcher.Matches(nullCollectionsItem, new TemplateListingFilter { Text = "Only" }));
        Assert.False(TemplateFilterMatcher.Matches(nullCollectionsItem, new TemplateListingFilter { Text = "SKU" }));
    }

    [Fact]
    public void TemplateFilterMatcher_Combinatorial_Shop_Status_Text_Matrix()
    {
        var items = new List<MockTemplateItem>
        {
            new() { ListingId = 1, ShopId = 1, State = "active", Title = "Blue Velvet Sofa", Skus = ["SOFA-BLU"], Tags = ["velvet"] },
            new() { ListingId = 2, ShopId = 1, State = "draft", Title = "Red Velvet Sofa", Skus = ["SOFA-RED"], Tags = ["velvet"] },
            new() { ListingId = 3, ShopId = 2, State = "active", Title = "Green Velvet Sofa", Skus = ["SOFA-GRN"], Tags = ["velvet"] },
            new() { ListingId = 4, ShopId = 2, State = "expired", Title = "Yellow Linen Sofa", Skus = ["SOFA-YLW"], Tags = ["linen"] },
            new() { ListingId = 5, ShopId = 1, State = "sold_out", Title = "Black Velvet Sofa", Skus = ["SOFA-BLK"], Tags = ["velvet"] },
            new() { ListingId = 6, ShopId = 1, State = "inactive", Title = "White Velvet Sofa", Skus = ["SOFA-WHT"], Tags = ["velvet"] },
            new() { ListingId = 7, ShopId = 1, State = "active", IsMissingOnEtsy = true, Title = "Ghost Sofa", Skus = ["SOFA-GHO"], Tags = ["ghost"] },
        };

        // Filter: ShopId=1, Status="active", Text="Velvet"
        var f1 = new TemplateListingFilter { ShopId = 1, Status = "active", Text = "Velvet" };
        var m1 = items.Where(i => TemplateFilterMatcher.Matches(i, f1)).Select(i => i.ListingId).ToList();
        Assert.Equal([1], m1); // Only Blue Velvet Sofa in Shop 1 active (Ghost Sofa excluded because IsMissingOnEtsy=true)

        // Filter: Status="missing"
        var fMissing = new TemplateListingFilter { Status = "missing" };
        var mMissing = items.Where(i => TemplateFilterMatcher.Matches(i, fMissing)).Select(i => i.ListingId).ToList();
        Assert.Equal([7], mMissing);

        // Filter: Status="all" must NOT include IsMissingOnEtsy item #7
        var fAll = new TemplateListingFilter { Status = "all" };
        var mAll = items.Where(i => TemplateFilterMatcher.Matches(i, fAll)).Select(i => i.ListingId).ToList();
        Assert.DoesNotContain(7, mAll);
        Assert.Equal(6, mAll.Count);

        // Status casing & whitespace resiliency: " SOLD_OUT "
        var fSoldOut = new TemplateListingFilter { Status = " SOLD_OUT " };
        var mSoldOut = items.Where(i => TemplateFilterMatcher.Matches(i, fSoldOut)).Select(i => i.ListingId).ToList();
        Assert.Equal([5], mSoldOut);
    }

    #endregion

    #region Section 4: R6 — Scoped Bulk Selection & Tri-State Transitions

    [Fact]
    public void BulkSelection_SingleKey_TriState_Cycle_And_FilteredTransitions()
    {
        var harness = new SingleKeySelectionHarness();
        for (var i = 1; i <= 6; i++)
        {
            harness.AllItems.Add(new MockTemplateItem
            {
                ListingId = i,
                ShopId = i <= 3 ? 1 : 2,
                Title = $"Listing {i} {(i % 2 == 0 ? "Even" : "Odd")}",
                State = "active"
            });
        }

        // 1. Initial state: 0 items picked -> Header = false
        Assert.False(harness.CandidateHeaderChecked);

        // 2. Toggle: all 6 items picked -> Header = true
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(6, harness.Picked.Count);

        // 3. Toggle again: all 6 items unpicked -> Header = false
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);
        Assert.Empty(harness.Picked);

        // 4. Pick 1 item manually -> Header = null (tri-state indeterminate)
        harness.Pick(1, true);
        Assert.Null(harness.CandidateHeaderChecked);

        // 5. Toggle from tri-state: all visible become picked -> Header = true
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(6, harness.Picked.Count);

        // 6. Filter to Shop 1 only (items 1, 2, 3 visible)
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        Assert.Equal(3, harness.Visible.Count);
        // All visible items (1, 2, 3) are in Picked -> Header is true
        Assert.True(harness.CandidateHeaderChecked);

        // 7. Toggle while filtered to Shop 1:
        // Visible items (1, 2, 3) are unpicked, but Shop 2 items (4, 5, 6) remain picked in background!
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked); // 0 of 3 visible are picked
        Assert.Equal([4, 5, 6], harness.Picked.OrderBy(x => x));

        // 8. Clear filter -> all 6 visible. Picked has 4, 5, 6 (3 of 6 picked)
        harness.Filter = new TemplateListingFilter();
        Assert.Null(harness.CandidateHeaderChecked); // Tri-state indeterminate

        // 9. Filter to non-matching query (0 visible items)
        harness.Filter = new TemplateListingFilter { Text = "NonExistentQuery999" };
        Assert.Empty(harness.Visible);
        Assert.False(harness.CandidateHeaderChecked); // 0 visible items -> false

        // Toggle on 0 visible items is a no-op; Picked remains [4, 5, 6]
        harness.TogglePickAllVisible();
        Assert.Equal([4, 5, 6], harness.Picked.OrderBy(x => x));
    }

    [Fact]
    public void BulkSelection_CompositeKey_TriState_Cycle_And_FilteredTransitions()
    {
        var harness = new CompositeKeySelectionHarness();
        // 4 composite items: Listing 1 with props 513 & 514; Listing 2 with prop 513; Listing 3 in Shop 2
        harness.AllItems.Add(new MockTemplateItem { ListingId = 1, PropertyId = 513L, ShopId = 1, Title = "Desk - Color" });
        harness.AllItems.Add(new MockTemplateItem { ListingId = 1, PropertyId = 514L, ShopId = 1, Title = "Desk - Size" });
        harness.AllItems.Add(new MockTemplateItem { ListingId = 2, PropertyId = 513L, ShopId = 1, Title = "Chair - Color" });
        harness.AllItems.Add(new MockTemplateItem { ListingId = 3, PropertyId = 513L, ShopId = 2, Title = "Table - Color" });

        // 1. Initial state: 0 picked -> Header = false
        Assert.False(harness.CandidateHeaderChecked);

        // 2. Pick composite (1, 513) -> Header = null (tri-state)
        harness.Pick(1, 513L, true);
        Assert.Null(harness.CandidateHeaderChecked);

        // 3. Toggle from tri-state -> all 4 visible picked -> Header = true
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(4, harness.Picked.Count);

        // 4. Filter by Text="Desk" -> 2 visible items: (1, 513) and (1, 514)
        harness.Filter = new TemplateListingFilter { Text = "Desk" };
        Assert.Equal(2, harness.Visible.Count);
        Assert.True(harness.CandidateHeaderChecked); // Both visible are picked

        // 5. Toggle while filtered -> Unpicks (1, 513) and (1, 514), but keeps (2, 513) and (3, 513)
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);
        Assert.Equal(2, harness.Picked.Count);
        Assert.Contains((2, 513L), harness.Picked);
        Assert.Contains((3, 513L), harness.Picked);

        // 6. Clear filter -> 4 visible, 2 picked -> Header is null
        harness.Filter = new TemplateListingFilter();
        Assert.Null(harness.CandidateHeaderChecked);
    }

    #endregion
}
