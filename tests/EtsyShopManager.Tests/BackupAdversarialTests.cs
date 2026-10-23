using System.IO.Compression;
using System.Text.Json;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Backups;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class BackupAdversarialTests
{
    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? UserName)> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityType, entityId, details, userName));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public Dictionary<string, string?> Settings { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string? value, bool isEncrypted = false, string? updatedBy = null, CancellationToken ct = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Settings.ContainsKey(key) && !string.IsNullOrEmpty(Settings[key]));
    }

    private sealed class FileBasedTestDbFactory(string connectionString) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task Empirically_verify_backup_zip_extraction_and_sqlite_integrity()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        // 1. Seed database with real schema and multiple entities
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            var shop = new Shop
            {
                Id = 1,
                EtsyShopId = 55555,
                EtsyUserId = 66666,
                Name = "Craft Woodworks",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            };
            db.Shops.Add(shop);

            var listing = new Listing
            {
                Id = 101,
                ShopId = 1,
                EtsyListingId = 77777,
                Title = "Handmade Walnut Table",
                Description = "A walnut table crafted with {{text.care}}.",
                PriceAmount = 149900,
                PriceDivisor = 100,
                Quantity = 5,
                State = "active",
                Tags = ["furniture", "walnut"],
                Materials = ["walnut wood"]
            };
            db.Listings.Add(listing);

            await db.SaveChangesAsync();
        }

        // 2. Add real media and key files
        var imgSub = Path.Combine(temp.Options.ImagesDirectory, "c1");
        Directory.CreateDirectory(imgSub);
        var originalImgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        var imgPath = Path.Combine(imgSub, "c1imgfile12345.jpg");
        await File.WriteAllBytesAsync(imgPath, originalImgBytes);

        var vidSub = Path.Combine(temp.Options.VideosDirectory, "d2");
        Directory.CreateDirectory(vidSub);
        var originalVidBytes = new byte[] { 0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D };
        var vidPath = Path.Combine(vidSub, "d2vidfile67890.mp4");
        await File.WriteAllBytesAsync(vidPath, originalVidBytes);

        var originalKeyText = "<key id=\"master\"><value>encrypted-dpapi-secret-payload</value></key>";
        var keyPath = Path.Combine(temp.Options.KeysDirectory, "key-master.xml");
        await File.WriteAllTextAsync(keyPath, originalKeyText);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // 3. Create restorable backup (using live null sourceConnection)
        var backupZipName = await service.CreateRestorableBackupAsync(user: "tester");
        var backupZipPath = Path.Combine(temp.Options.BackupsDirectory, backupZipName);
        Assert.True(File.Exists(backupZipPath), "Backup zip file was not created.");

        // 4. Empirically EXTRACT the entire zip to a temporary folder
        var extractDir = Path.Combine(Path.GetTempPath(), "esm-backup-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(backupZipPath, extractDir);

            // 5. Verify manifest.json
            var manifestPath = Path.Combine(extractDir, "manifest.json");
            Assert.True(File.Exists(manifestPath), "manifest.json missing in extracted archive.");
            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("full_restore", root.GetProperty("backup_type").GetString());
            Assert.Equal(1, root.GetProperty("image_count").GetInt32());
            Assert.Equal(1, root.GetProperty("video_count").GetInt32());
            Assert.Equal(1, root.GetProperty("key_count").GetInt32());
            Assert.True(root.GetProperty("database_size_bytes").GetInt64() > 0);

            // 6. Verify etsyshopmanager.db with SQLite
            var extractedDbPath = Path.Combine(extractDir, "etsyshopmanager.db");
            Assert.True(File.Exists(extractedDbPath), "etsyshopmanager.db missing in extracted archive.");

            await using (var conn = new SqliteConnection($"Data Source={extractedDbPath};Pooling=False"))
            {
                await conn.OpenAsync();

                // PRAGMA integrity_check
                await using var pragmaCmd = conn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA integrity_check;";
                var integrity = (string?)await pragmaCmd.ExecuteScalarAsync();
                Assert.Equal("ok", integrity);

                // Verify tables and seeded data
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM Shops WHERE EtsyShopId = 55555 AND Name = 'Craft Woodworks';";
                var shopCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                Assert.Equal(1, shopCount);

                checkCmd.CommandText = "SELECT Title, PriceAmount, Quantity FROM Listings WHERE EtsyListingId = 77777;";
                await using var reader = await checkCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Handmade Walnut Table", reader.GetString(0));
                Assert.Equal(149900L, reader.GetInt64(1));
                Assert.Equal(5, reader.GetInt32(2));
            }

            // 7. Verify media and keys files in extracted directory
            var extractedImg = Path.Combine(extractDir, "images", "c1", "c1imgfile12345.jpg");
            Assert.True(File.Exists(extractedImg), "Extracted image file does not exist.");
            Assert.Equal(originalImgBytes, await File.ReadAllBytesAsync(extractedImg));

            var extractedVid = Path.Combine(extractDir, "videos", "d2", "d2vidfile67890.mp4");
            Assert.True(File.Exists(extractedVid), "Extracted video file does not exist.");
            Assert.Equal(originalVidBytes, await File.ReadAllBytesAsync(extractedVid));

            var extractedKey = Path.Combine(extractDir, "keys", "key-master.xml");
            Assert.True(File.Exists(extractedKey), "Extracted key file does not exist.");
            Assert.Equal(originalKeyText, await File.ReadAllTextAsync(extractedKey));
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Empirically_verify_listing_export_zip_extraction_and_hierarchy()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            // Shop 1
            var shop1 = new Shop
            {
                Id = 1,
                EtsyShopId = 11111,
                EtsyUserId = 22222,
                Name = "Northern Oak & Maple / Studio",
                Url = "https://etsy.com/shop/NorthernOakStudio",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            };
            db.Shops.Add(shop1);

            // Listing 1
            var listing1 = new Listing
            {
                Id = 10,
                ShopId = 1,
                EtsyListingId = 90001,
                Title = "Live Edge Maple Coffee Table (100% Solid!)",
                Description = "Rendered live edge description for Etsy.",
                PriceAmount = 45000,
                PriceDivisor = 100,
                Quantity = 2,
                State = "active",
                Tags = ["table", "maple", "furniture"],
                Materials = ["maple", "steel"]
            };
            db.Listings.Add(listing1);

            db.ListingDescriptions.Add(new ListingDescription
            {
                ListingId = 10,
                Source = "Source template description with {{shop.name}} and {{text.care-guide}}.",
                Rendered = "Rendered live edge description for Etsy.",
                UpdatedAtUtc = DateTime.UtcNow
            });

            var imgFileName = "e5photo1234567890abcdef.jpg";
            db.ListingImages.Add(new ListingImage
            {
                ListingId = 10,
                EtsyImageId = 5001,
                Rank = 1,
                AltText = "Top view",
                FileName = imgFileName,
                SourceUrl = "https://example.com/maple.jpg"
            });

            var vidFileName = "f6video1234567890abcdef.mp4";
            db.ListingVideos.Add(new ListingVideo
            {
                ListingId = 10,
                EtsyVideoId = 6001,
                Width = 1920,
                Height = 1080,
                VideoState = "active",
                FileName = vidFileName,
                SourceUrl = "https://example.com/maple.mp4"
            });

            // Shop 2
            var shop2 = new Shop
            {
                Id = 2,
                EtsyShopId = 33333,
                EtsyUserId = 44444,
                Name = "Rustic Ironworks",
                CurrencyCode = "CAD",
                ConnectedAtUtc = DateTime.UtcNow
            };
            db.Shops.Add(shop2);

            var listing2 = new Listing
            {
                Id = 20,
                ShopId = 2,
                EtsyListingId = 90002,
                Title = "Forged Iron Hook",
                Description = "Simple iron hook without separate template description.",
                PriceAmount = 2500,
                PriceDivisor = 100,
                Quantity = 20,
                State = "draft"
            };
            db.Listings.Add(listing2);

            await db.SaveChangesAsync();
        }

        // Add media files to store
        var imgDir = Path.Combine(temp.Options.ImagesDirectory, "e5");
        Directory.CreateDirectory(imgDir);
        var testImgBytes = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        await File.WriteAllBytesAsync(Path.Combine(imgDir, "e5photo1234567890abcdef.jpg"), testImgBytes);

        var vidDir = Path.Combine(temp.Options.VideosDirectory, "f6");
        Directory.CreateDirectory(vidDir);
        var testVidBytes = new byte[] { 0x66, 0x77, 0x88, 0x99 };
        await File.WriteAllBytesAsync(Path.Combine(vidDir, "f6video1234567890abcdef.mp4"), testVidBytes);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var exportZipName = await service.CreateListingExportAsync(user: "exporter");
        var exportZipPath = Path.Combine(temp.Options.BackupsDirectory, exportZipName);
        Assert.True(File.Exists(exportZipPath), "Export zip was not created.");

        // Empirically EXTRACT the entire export zip to a temporary folder
        var extractDir = Path.Combine(Path.GetTempPath(), "esm-export-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(exportZipPath, extractDir);

            // 1. Root manifest.json
            var manifestPath = Path.Combine(extractDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                var r = doc.RootElement;
                Assert.Equal("listings_export", r.GetProperty("backup_type").GetString());
                Assert.Equal(2, r.GetProperty("shop_count").GetInt32());
                Assert.Equal(2, r.GetProperty("listing_count").GetInt32());
            }

            // 2. Shop 1 directory: slugified name "northern-oak-maple-studio"
            var shop1Dir = Path.Combine(extractDir, "shops", "1_northern-oak-maple-studio");
            Assert.True(Directory.Exists(shop1Dir), $"Expected shop directory {shop1Dir} not found.");

            var shop1Json = Path.Combine(shop1Dir, "shop.json");
            Assert.True(File.Exists(shop1Json));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(shop1Json)))
            {
                Assert.Equal("Northern Oak & Maple / Studio", doc.RootElement.GetProperty("name").GetString());
                Assert.Equal(11111, doc.RootElement.GetProperty("etsy_shop_id").GetInt64());
            }

            // Listing 1 directory under Shop 1: "90001_live-edge-maple-coffee-table-100-solid"
            var listing1Dir = Path.Combine(shop1Dir, "listings", "90001_live-edge-maple-coffee-table-100-solid");
            Assert.True(Directory.Exists(listing1Dir), $"Expected listing directory {listing1Dir} not found.");

            // listing.json
            var listing1Json = Path.Combine(listing1Dir, "listing.json");
            Assert.True(File.Exists(listing1Json));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(listing1Json)))
            {
                var el = doc.RootElement;
                Assert.Equal("Live Edge Maple Coffee Table (100% Solid!)", el.GetProperty("title").GetString());
                Assert.Equal(45000, el.GetProperty("price_amount").GetInt64());
                Assert.Equal(2, el.GetProperty("quantity").GetInt32());
            }

            // description.txt (preserves placeholders)
            var desc1Path = Path.Combine(listing1Dir, "description.txt");
            Assert.True(File.Exists(desc1Path));
            var desc1Text = await File.ReadAllTextAsync(desc1Path);
            Assert.Equal("Source template description with {{shop.name}} and {{text.care-guide}}.", desc1Text);

            // rendered_description.txt
            var rend1Path = Path.Combine(listing1Dir, "rendered_description.txt");
            Assert.True(File.Exists(rend1Path));
            var rend1Text = await File.ReadAllTextAsync(rend1Path);
            Assert.Equal("Rendered live edge description for Etsy.", rend1Text);

            // media files
            var photoPath = Path.Combine(listing1Dir, "media", "photo_01_e5photo1234567890abcdef.jpg");
            Assert.True(File.Exists(photoPath));
            Assert.Equal(testImgBytes, await File.ReadAllBytesAsync(photoPath));

            var videoPath = Path.Combine(listing1Dir, "media", "video_f6video1234567890abcdef.mp4");
            Assert.True(File.Exists(videoPath));
            Assert.Equal(testVidBytes, await File.ReadAllBytesAsync(videoPath));

            // 3. Shop 2 and Listing 2
            var shop2Dir = Path.Combine(extractDir, "shops", "2_rustic-ironworks");
            Assert.True(Directory.Exists(shop2Dir));
            var listing2Dir = Path.Combine(shop2Dir, "listings", "90002_forged-iron-hook");
            Assert.True(Directory.Exists(listing2Dir));

            // When no ListingDescription row exists, description.txt falls back to listing.Description
            var desc2Text = await File.ReadAllTextAsync(Path.Combine(listing2Dir, "description.txt"));
            Assert.Equal("Simple iron hook without separate template description.", desc2Text);
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData(@"..\secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\windows\win.ini")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("/etc/shadow")]
    [InlineData("backup-20261021-120000.zip/..")]
    [InlineData(@"backup-20261021-120000.zip\..")]
    [InlineData("../backup-20261021-120000.zip")]
    [InlineData(@"..\backup-20261021-120000.zip")]
    [InlineData("backup-20261021-120000.zip/sub")]
    [InlineData(@"backup-20261021-120000.zip\sub")]
    [InlineData("backup-20261021-120000.zip:stream")]
    [InlineData("backup-20261021-120000.zip\0")]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData("deploy-20261021-120000")]
    [InlineData("backup-20261021-120000.zip.bak")]
    [InlineData("backup-20261021-12000.zip")]
    [InlineData("backup-20261021-1200000.zip")]
    [InlineData("etsy-listings-export-20261021-120000.zip/..")]
    [InlineData(@"etsy-listings-export-20261021-120000.zip\..")]
    [InlineData("etsyshopmanager.db")]
    public async Task Path_traversal_attempts_are_strictly_rejected_by_GetBackupPath_and_DeleteBackupAsync(string maliciousInput)
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // GetBackupPath must throw ArgumentException
        Assert.Throws<ArgumentException>(() => service.GetBackupPath(maliciousInput));

        // DeleteBackupAsync must throw ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteBackupAsync(maliciousInput));

        // Sync DeleteBackup must throw ArgumentException
        Assert.Throws<ArgumentException>(() => service.DeleteBackup(maliciousInput));
    }

    [Fact]
    public async Task Path_handling_behavior_for_valid_filenames()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // 1. Non-existent valid backup file
        const string nonExistent = "backup-20260101-000000.zip";
        Assert.Throws<FileNotFoundException>(() => service.GetBackupPath(nonExistent));
        var deleted = await service.DeleteBackupAsync(nonExistent);
        Assert.False(deleted);

        // 2. Existing valid backup file
        const string validFile = "backup-20261021-120000.zip";
        var validPath = Path.Combine(temp.Options.BackupsDirectory, validFile);
        await File.WriteAllBytesAsync(validPath, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var resolvedPath = service.GetBackupPath(validFile);
        Assert.Equal(validPath, resolvedPath);

        var deleteSuccess = await service.DeleteBackupAsync(validFile, user: "admin");
        Assert.True(deleteSuccess);
        Assert.False(File.Exists(validPath));
    }

    [Fact]
    public async Task Retention_pruning_stress_test_with_mixed_archives_and_invariants()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var baseTime = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = baseTime };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // Create 20 backup archives with timestamps spaced 1 day apart
        var backupPaths = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var date = baseTime.AddDays(-i).UtcDateTime;
            var name = $"backup-{date:yyyyMMdd-HHmmss}.zip";
            var p = Path.Combine(temp.Options.BackupsDirectory, name);
            await File.WriteAllBytesAsync(p, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            backupPaths.Add(p);
        }

        // Add non-backup files: deploy directory and export archive
        var deployDir = Path.Combine(temp.Options.BackupsDirectory, "deploy-20261001-000000");
        Directory.CreateDirectory(deployDir);
        var exportArchive = Path.Combine(temp.Options.BackupsDirectory, "etsy-listings-export-20261021-120000.zip");
        await File.WriteAllBytesAsync(exportArchive, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        // Keep 5 backups max, retention 10 days
        var pruned = await service.PruneBackupsAsync(retentionDays: 10, maxCount: 5, user: "pruner");
        Assert.Equal(15, pruned);

        // Newest 5 must exist
        for (var i = 0; i < 5; i++)
        {
            Assert.True(File.Exists(backupPaths[i]), $"Backup {backupPaths[i]} should have been kept.");
        }

        // Older 15 must be pruned
        for (var i = 5; i < 20; i++)
        {
            Assert.False(File.Exists(backupPaths[i]), $"Backup {backupPaths[i]} should have been pruned.");
        }

        // Deploy dir and export zip MUST remain untouched
        Assert.True(Directory.Exists(deployDir));
        Assert.True(File.Exists(exportArchive));
    }

    [Fact]
    public async Task Retention_pruning_with_zero_settings_keeps_all_backups()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var time = new ImmediateTimeProvider { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        var b1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20200101-120000.zip");
        var b2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261021-120000.zip");
        await File.WriteAllBytesAsync(b1, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        await File.WriteAllBytesAsync(b2, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        // retentionDays = 0 (keep forever), maxCount = 0 (unlimited)
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 0);
        Assert.Equal(0, pruned);
        Assert.True(File.Exists(b1));
        Assert.True(File.Exists(b2));
    }
}
