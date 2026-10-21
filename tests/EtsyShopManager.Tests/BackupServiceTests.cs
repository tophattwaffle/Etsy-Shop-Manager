using System.IO.Compression;
using System.Text.Json;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Backups;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class BackupServiceTests
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

        public AppDbContext CreateDbContext()
        {
            var db = new AppDbContext(_options);
            return db;
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task Online_snapshot_produces_a_valid_queryable_database()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                EtsyShopId = 12345,
                EtsyUserId = 67890,
                Name = "Test Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var snapshotPath = Path.Combine(temp.Options.BackupsDirectory, "test-snapshot.db");
        await service.CreateDatabaseSnapshotAsync(snapshotPath);

        Assert.True(File.Exists(snapshotPath), "Snapshot database file was not created.");

        // Query the snapshot database directly to verify integrity
        await using var destConn = new SqliteConnection($"Data Source={snapshotPath}");
        await destConn.OpenAsync();
        await using var cmd = destConn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Shops WHERE EtsyShopId = 12345;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateRestorableBackup_creates_valid_zip_with_manifest_db_keys_and_images()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                EtsyShopId = 99999,
                EtsyUserId = 88888,
                Name = "Snapshot Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Add dummy images, videos, and keys
        var imgSub = Path.Combine(temp.Options.ImagesDirectory, "ab");
        Directory.CreateDirectory(imgSub);
        var imgFile = Path.Combine(imgSub, "abcdef123456.jpg");
        await File.WriteAllBytesAsync(imgFile, [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02]);

        var vidSub = Path.Combine(temp.Options.VideosDirectory, "12");
        Directory.CreateDirectory(vidSub);
        var vidFile = Path.Combine(vidSub, "1234567890.mp4");
        await File.WriteAllBytesAsync(vidFile, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);

        var keyFile = Path.Combine(temp.Options.KeysDirectory, "key-test.xml");
        await File.WriteAllTextAsync(keyFile, "<key id=\"test\"><value>secret</value></key>");

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var zipName = await service.CreateRestorableBackupAsync(user: "test-admin");

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, zipName);
        Assert.True(File.Exists(zipPath), "Backup zip file was not created.");

        // Inspect Zip contents
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);

        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("full_restore", root.GetProperty("backup_type").GetString());
            Assert.True(root.GetProperty("database_size_bytes").GetInt64() > 0);
            Assert.Equal(1, root.GetProperty("image_count").GetInt32());
            Assert.Equal(1, root.GetProperty("video_count").GetInt32());
            Assert.Equal(1, root.GetProperty("key_count").GetInt32());
        }

        Assert.NotNull(archive.GetEntry("etsyshopmanager.db"));
        Assert.NotNull(archive.GetEntry("images/ab/abcdef123456.jpg"));
        Assert.NotNull(archive.GetEntry("videos/12/1234567890.mp4"));
        Assert.NotNull(archive.GetEntry("keys/key-test.xml"));

        // Verify audit log
        Assert.Contains(audit.Entries, e => e.Action == "backup.created" && e.Details!.Contains(zipName));
    }

    [Fact]
    public async Task Retention_pruning_removes_old_archives_and_beyond_max_count_while_preserving_newest()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var time = new ImmediateTimeProvider
        {
            Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)
        };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // Create 5 archives of varying ages
        var b1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261001-120000.zip"); // 20 days old
        var b2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261010-120000.zip"); // 11 days old
        var b3 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261015-120000.zip"); // 6 days old
        var b4 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261018-120000.zip"); // 3 days old
        var b5 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261021-120000.zip"); // today (newest)

        foreach (var file in new[] { b1, b2, b3, b4, b5 })
        {
            await File.WriteAllBytesAsync(file, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        }

        // Create deploy folder & portable export to ensure they are untouched
        var deployDir = Path.Combine(temp.Options.BackupsDirectory, "deploy-20261001-120000");
        Directory.CreateDirectory(deployDir);
        var exportArchive = Path.Combine(temp.Options.BackupsDirectory, "etsy-listings-export-20261001-120000.zip");
        await File.WriteAllBytesAsync(exportArchive, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        // Retention: 10 days, maxCount: 2
        var pruned = await service.PruneBackupsAsync(retentionDays: 10, maxCount: 2, user: "test-pruner");

        Assert.Equal(3, pruned);

        // Invariant check: Deploy directory and listings export must never be deleted
        Assert.True(Directory.Exists(deployDir), "Deploy directory must not be deleted.");
        Assert.True(File.Exists(exportArchive), "Listing export archive must not be deleted.");

        // Invariant check: Newest backup (b5) and b4 (second newest, within count 2 and age 10d) must remain
        Assert.True(File.Exists(b5), "Newest backup must be kept.");
        Assert.True(File.Exists(b4), "Second newest backup must be kept.");

        // Older and beyond maxCount backups must be deleted
        Assert.False(File.Exists(b3), "b3 exceeded maxCount 2 and should be pruned.");
        Assert.False(File.Exists(b2), "b2 exceeded retentionDays 10 and should be pruned.");
        Assert.False(File.Exists(b1), "b1 exceeded retentionDays 10 and should be pruned.");

        // Audit log checks
        Assert.Equal(3, audit.Entries.Count(e => e.Action == "backup.pruned"));
    }

    [Fact]
    public async Task Retention_pruning_safety_invariant_always_keeps_newest_backup_even_with_aggressive_settings()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var time = new ImmediateTimeProvider
        {
            Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)
        };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // Single ancient backup (6 years old)
        var oldBackup = Path.Combine(temp.Options.BackupsDirectory, "backup-20200101-120000.zip");
        await File.WriteAllBytesAsync(oldBackup, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        // Aggressive pruning: retentionDays: 1, maxCount: 1
        var pruned = await service.PruneBackupsAsync(retentionDays: 1, maxCount: 1);

        Assert.Equal(0, pruned);
        Assert.True(File.Exists(oldBackup), "Safety invariant: Single newest backup must NEVER be deleted!");
    }

    [Fact]
    public async Task CreateListingExport_creates_portable_zip_with_shops_listings_media_and_descriptions()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var shop = new Shop
            {
                Id = 1,
                EtsyShopId = 42,
                EtsyUserId = 100,
                Name = "Oak & Iron",
                Url = "https://etsy.com/shop/OakAndIron",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            };
            db.Shops.Add(shop);

            var listing = new Listing
            {
                Id = 10,
                ShopId = 1,
                EtsyListingId = 987654,
                Title = "Handmade Oak Desk",
                Description = "Beautiful solid oak desk with {{text.care-instructions}}.",
                PriceAmount = 89900,
                PriceDivisor = 100,
                Quantity = 3,
                State = "active",
                Tags = ["furniture", "desk", "oak"],
                Materials = ["solid oak", "steel legs"]
            };
            db.Listings.Add(listing);

            db.ListingDescriptions.Add(new ListingDescription
            {
                ListingId = 10,
                Source = "Beautiful solid oak desk with {{text.care-instructions}}.",
                Rendered = "Beautiful solid oak desk with Hand wipe with dry cloth.",
                UpdatedAtUtc = DateTime.UtcNow
            });

            db.ListingImages.Add(new ListingImage
            {
                ListingId = 10,
                EtsyImageId = 1111,
                Rank = 1,
                AltText = "Front view",
                FileName = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789.jpg",
                SourceUrl = "https://i.etsystatic.com/img.jpg"
            });

            var product = new ListingProduct
            {
                ListingId = 10,
                EtsyProductId = 2222,
                Sku = "DESK-OAK-STD",
                PropertyValues = [new PropertyValue { PropertyId = 1, PropertyName = "Finish", Values = ["Natural"] }]
            };
            product.Offerings.Add(new ListingOffering
            {
                PriceAmount = 89900,
                PriceDivisor = 100,
                Quantity = 3,
                IsEnabled = true
            });
            db.ListingProducts.Add(product);

            await db.SaveChangesAsync();
        }

        // Put media file in image store
        var imgName = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789.jpg";
        var imgDir = Path.Combine(temp.Options.ImagesDirectory, imgName[..2]);
        Directory.CreateDirectory(imgDir);
        var mediaBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22, 0x33, 0x44 };
        await File.WriteAllBytesAsync(Path.Combine(imgDir, imgName), mediaBytes);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var exportZipName = await service.CreateListingExportAsync(user: "exporter");
        var exportZipPath = Path.Combine(temp.Options.BackupsDirectory, exportZipName);

        Assert.True(File.Exists(exportZipPath), "Listing export zip was not created.");

        using var archive = ZipFile.OpenRead(exportZipPath);
        Assert.NotNull(archive.GetEntry("manifest.json"));

        // Verify shop.json
        var shopEntry = archive.GetEntry("shops/1_oak-iron/shop.json");
        Assert.NotNull(shopEntry);
        using (var reader = new StreamReader(shopEntry.Open()))
        {
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Oak & Iron", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("etsy_shop_id").GetInt64());
        }

        // Verify listing.json
        var listingEntry = archive.GetEntry("shops/1_oak-iron/listings/987654_handmade-oak-desk/listing.json");
        Assert.NotNull(listingEntry);
        using (var reader = new StreamReader(listingEntry.Open()))
        {
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Handmade Oak Desk", doc.RootElement.GetProperty("title").GetString());
            Assert.Equal(89900, doc.RootElement.GetProperty("price_amount").GetInt64());
        }

        // Verify description.txt has placeholders
        var descEntry = archive.GetEntry("shops/1_oak-iron/listings/987654_handmade-oak-desk/description.txt");
        Assert.NotNull(descEntry);
        using (var reader = new StreamReader(descEntry.Open()))
        {
            var text = await reader.ReadToEndAsync();
            Assert.Contains("{{text.care-instructions}}", text);
        }

        // Verify rendered_description.txt
        var renderedEntry = archive.GetEntry("shops/1_oak-iron/listings/987654_handmade-oak-desk/rendered_description.txt");
        Assert.NotNull(renderedEntry);

        // Verify photo file in media
        var mediaEntry = archive.GetEntry($"shops/1_oak-iron/listings/987654_handmade-oak-desk/media/photo_01_{imgName}");
        Assert.NotNull(mediaEntry);
        using (var ms = new MemoryStream())
        {
            using (var stream = mediaEntry.Open())
            {
                await stream.CopyToAsync(ms);
            }
            Assert.Equal(mediaBytes, ms.ToArray());
        }

        Assert.Contains(audit.Entries, e => e.Action == "backup.export-created" && e.Details!.Contains(exportZipName));
    }

    [Fact]
    public async Task ListBackups_and_DeleteBackup_validate_paths_and_manage_files()
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

        var rZip = Path.Combine(temp.Options.BackupsDirectory, "backup-20261021-100000.zip");
        var eZip = Path.Combine(temp.Options.BackupsDirectory, "etsy-listings-export-20261021-110000.zip");
        var otherFile = Path.Combine(temp.Options.BackupsDirectory, "notes.txt");

        await File.WriteAllBytesAsync(rZip, [1, 2, 3]);
        await File.WriteAllBytesAsync(eZip, [4, 5, 6]);
        await File.WriteAllBytesAsync(otherFile, [7, 8, 9]);

        var list = service.ListBackups();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, b => b.FileName == "backup-20261021-100000.zip" && b.BackupType == "Restorable");
        Assert.Contains(list, b => b.FileName == "etsy-listings-export-20261021-110000.zip" && b.BackupType == "Export");

        // Valid delete
        var deleted = await service.DeleteBackupAsync("backup-20261021-100000.zip", user: "admin");
        Assert.True(deleted);
        Assert.False(File.Exists(rZip));
        Assert.Contains(audit.Entries, e => e.Action == "backup.deleted" && e.Details!.Contains("backup-20261021-100000.zip"));

        // Path traversal guard
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteBackupAsync("../notes.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteBackupAsync("notes.txt")); // Not a recognized backup zip
    }

    [Fact]
    public async Task CreateBackupJob_executes_and_sets_result()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var job = new CreateBackupJob(service);
        var context = new JobExecutionContext
        {
            JobId = 100,
            JobType = CreateBackupJob.Type,
            Attempt = 1
        };

        await job.HandleAsync(context, CancellationToken.None);

        Assert.NotNull(context.Result);
        Assert.StartsWith("Created backup: backup-", context.Result);

        var backups = service.ListBackups();
        Assert.Single(backups);
        Assert.Equal("Restorable", backups[0].BackupType);
    }
}
