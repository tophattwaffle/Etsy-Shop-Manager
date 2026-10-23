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
        public bool ClearCacheCalled { get; set; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string? value, bool isEncrypted = false, string? updatedBy = null, CancellationToken ct = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Settings.ContainsKey(key) && !string.IsNullOrEmpty(Settings[key]));

        public void ClearCache() => ClearCacheCalled = true;
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

    [Theory]
    [InlineData(@"Dropbox\EtsyBackups")]
    [InlineData(@"Dropbox - Business\Backups")]
    [InlineData(@"OneDrive\Backups")]
    [InlineData(@"OneDrive - Company\Backups")]
    public async Task Configurable_backup_directory_uses_custom_path_including_dropbox_or_onedrive(string folderName)
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        var customBackupDir = Path.Combine(Path.GetTempPath(), "esm-test-cloud", folderName);
        if (Directory.Exists(customBackupDir)) Directory.Delete(customBackupDir, recursive: true);

        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.Shops.Add(new Shop
                {
                    EtsyShopId = 1111,
                    EtsyUserId = 2222,
                    Name = "Cloud Shop",
                    CurrencyCode = "USD",
                    ConnectedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            await settings.SetAsync(SettingKeys.BackupsDirectory, customBackupDir);

            var service = new BackupService(
                temp.Options,
                factory,
                settings,
                audit,
                TimeProvider.System,
                NullLogger<BackupService>.Instance);

            var resolved = await service.GetBackupsDirectoryAsync();
            Assert.Equal(Path.GetFullPath(customBackupDir), resolved);
            Assert.True(Directory.Exists(resolved), "Configured directory must be created.");

            // Create restorable backup
            var fileName = await service.CreateRestorableBackupAsync(user: "admin");
            var finalZip = Path.Combine(resolved, fileName);
            Assert.True(File.Exists(finalZip), "Backup archive must be written to custom directory.");
            Assert.False(File.Exists(Path.Combine(temp.Options.BackupsDirectory, fileName)), "Backup archive must NOT be in default directory.");

            // List backups
            var list = service.ListBackups();
            Assert.Single(list);
            Assert.Equal(fileName, list[0].FileName);

            // Prune backups
            var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 1);
            Assert.Equal(0, pruned); // Single backup preserved by invariant
            Assert.True(File.Exists(finalZip));
        }
        finally
        {
            try { if (Directory.Exists(customBackupDir)) Directory.Delete(customBackupDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Configurable_backup_directory_falls_back_to_storage_options_when_blank(string? configuredPath)
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        if (configuredPath is not null)
        {
            await settings.SetAsync(SettingKeys.BackupsDirectory, configuredPath);
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var resolvedAsync = await service.GetBackupsDirectoryAsync();
        Assert.Equal(temp.Options.BackupsDirectory, resolvedAsync);

        var resolvedSync = service.GetBackupsDirectory();
        Assert.Equal(temp.Options.BackupsDirectory, resolvedSync);
    }

    [Fact]
    public void ProbeBackupDirectory_validates_syntax_and_writeability()
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

        // Blank/null probes default directory
        var defaultProbe = service.ProbeBackupDirectory(null);
        Assert.True(defaultProbe.Success);
        Assert.Equal(temp.Options.BackupsDirectory, defaultProbe.ResolvedPath);

        // Valid writable directory
        var customPath = Path.Combine(Path.GetTempPath(), "esm-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var validProbe = service.ProbeBackupDirectory(customPath);
            Assert.True(validProbe.Success);
            Assert.Equal(Path.GetFullPath(customPath), validProbe.ResolvedPath);
            Assert.True(Directory.Exists(customPath));
        }
        finally
        {
            try { if (Directory.Exists(customPath)) Directory.Delete(customPath, recursive: true); } catch { }
        }

        // Environment variable expansion
        var envProbe = service.ProbeBackupDirectory(@"%TEMP%\esm-probe-env");
        Assert.True(envProbe.Success);
        Assert.DoesNotContain("%TEMP%", envProbe.ResolvedPath!);

        // Invalid path syntax
        var invalidProbe = service.ProbeBackupDirectory("Z:\\invalid<?>path");
        Assert.False(invalidProbe.Success);
        Assert.NotNull(invalidProbe.ErrorMessage);
    }

    [Fact]
    public async Task ValidateBackupArchive_and_Stream_verify_archive_integrity()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 1, EtsyUserId = 2, Name = "VShop", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // 1. Valid restorable backup
        var validZipName = await service.CreateRestorableBackupAsync();
        var validZipPath = Path.Combine(temp.Options.BackupsDirectory, validZipName);

        var validResult = await service.ValidateBackupArchiveAsync(validZipPath);
        Assert.True(validResult.IsValid);
        Assert.Null(validResult.ErrorMessage);
        Assert.Equal("full_restore", validResult.BackupType);
        Assert.True(validResult.DbSizeBytes > 0);

        // Stream validation matches
        await using (var stream = File.OpenRead(validZipPath))
        {
            var streamResult = await service.ValidateBackupStreamAsync(stream);
            Assert.True(streamResult.IsValid);
            Assert.Equal(validResult.DbSizeBytes, streamResult.DbSizeBytes);
        }

        // 2. Corrupt zip file
        var corruptPath = Path.Combine(temp.Options.BackupsDirectory, "backup-corrupt.zip");
        await File.WriteAllBytesAsync(corruptPath, [1, 2, 3, 4, 5]);
        var corruptResult = await service.ValidateBackupArchiveAsync(corruptPath);
        Assert.False(corruptResult.IsValid);
        Assert.NotNull(corruptResult.ErrorMessage);

        // 3. Listings export (should be rejected as full_restore)
        var exportName = await service.CreateListingExportAsync();
        var exportPath = Path.Combine(temp.Options.BackupsDirectory, exportName);
        var exportResult = await service.ValidateBackupArchiveAsync(exportPath);
        Assert.False(exportResult.IsValid);
        Assert.Contains("Only 'full_restore' archives can be restored", exportResult.ErrorMessage!);

        // 4. Missing manifest.json
        var noManifestPath = Path.Combine(temp.Options.BackupsDirectory, "backup-no-manifest.zip");
        using (var zip = ZipFile.Open(noManifestPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("dummy.txt");
        }
        var noManifestResult = await service.ValidateBackupArchiveAsync(noManifestPath);
        Assert.False(noManifestResult.IsValid);

        // 5. Path traversal entry
        var traversalPath = Path.Combine(temp.Options.BackupsDirectory, "backup-traversal.zip");
        using (var zip = ZipFile.Open(traversalPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("../evil.txt");
        }
        var traversalResult = await service.ValidateBackupArchiveAsync(traversalPath);
        Assert.False(traversalResult.IsValid);
        Assert.Contains("malicious entry path", traversalResult.ErrorMessage!);

        // 6. Unauthorized entry
        var unauthorizedPath = Path.Combine(temp.Options.BackupsDirectory, "backup-unauth.zip");
        using (var zip = ZipFile.Open(unauthorizedPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
            zip.CreateEntry("virus.exe");
        }
        var unauthResult = await service.ValidateBackupArchiveAsync(unauthorizedPath);
        Assert.False(unauthResult.IsValid);
        Assert.Contains("unauthorized entry", unauthResult.ErrorMessage!);
    }

    [Fact]
    public async Task RestoreBackup_creates_safety_backup_and_restores_database_media_and_keys()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        // 1. Initial State: Shop 1, an image, a video, a key
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 100,
                EtsyUserId = 200,
                Name = "Original Shop 1",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var imgDir = Path.Combine(temp.Options.ImagesDirectory, "orig");
        Directory.CreateDirectory(imgDir);
        var origImgFile = Path.Combine(imgDir, "orig-img.jpg");
        await File.WriteAllBytesAsync(origImgFile, [0xAA, 0xBB, 0xCC]);

        var vidDir = Path.Combine(temp.Options.VideosDirectory, "orig");
        Directory.CreateDirectory(vidDir);
        var origVidFile = Path.Combine(vidDir, "orig-vid.mp4");
        await File.WriteAllBytesAsync(origVidFile, [0x11, 0x22, 0x33]);

        var origKeyFile = Path.Combine(temp.Options.KeysDirectory, "orig-key.xml");
        await File.WriteAllTextAsync(origKeyFile, "<key>orig</key>");

        var time = new ImmediateTimeProvider
        {
            Now = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero)
        };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // Create Backup 1 (the archive we will restore later)
        var backup1FileName = await service.CreateRestorableBackupAsync(user: "setup");
        var backup1Path = Path.Combine(temp.Options.BackupsDirectory, backup1FileName);
        Assert.True(File.Exists(backup1Path));

        // 2. Mutate live state: add Shop 2, change Shop 1 name, mutate image file, add new video
        time.Now = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            var shop1 = await db.Shops.FirstAsync(s => s.Id == 1);
            shop1.Name = "Mutated Shop 1";
            db.Shops.Add(new Shop
            {
                Id = 2,
                EtsyShopId = 555,
                EtsyUserId = 666,
                Name = "Mutated Shop 2",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await File.WriteAllBytesAsync(origImgFile, [0x99, 0x99, 0x99]); // mutated bytes
        var newVidFile = Path.Combine(vidDir, "mutated-vid.mp4");
        await File.WriteAllBytesAsync(newVidFile, [0x88, 0x88]);

        // 3. Restore from Backup 1
        var restoreResult = await service.RestoreBackupAsync(backup1FileName, user: "admin");
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.NotNull(restoreResult.PreRestoreSafetyBackup);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, restoreResult.PreRestoreSafetyBackup)),
            "Pre-restore safety backup must exist on disk.");

        // 4. Assert live DB was restored to Backup 1 state
        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.ToListAsync();
            Assert.Single(shops);
            Assert.Equal(1, shops[0].Id);
            Assert.Equal("Original Shop 1", shops[0].Name);
        }

        // 5. Assert media and keys restored
        var restoredImgBytes = await File.ReadAllBytesAsync(origImgFile);
        Assert.Equal([0xAA, 0xBB, 0xCC], restoredImgBytes);

        var restoredKeyText = await File.ReadAllTextAsync(origKeyFile);
        Assert.Equal("<key>orig</key>", restoredKeyText);

        // 6. Assert settings cache was cleared
        Assert.True(settings.ClearCacheCalled, "settings.ClearCache() must be called on restore.");

        // 7. Assert audit log contains backup.restored
        Assert.Contains(audit.Entries, e => e.Action == "backup.restored" && e.Details!.Contains(restoreResult.PreRestoreSafetyBackup));
    }

    [Fact]
    public async Task RestoreBackupFromStream_restores_uploaded_zip_stream()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 42, EtsyUserId = 99, Name = "Stream Shop", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var backupFileName = await service.CreateRestorableBackupAsync();
        var backupPath = Path.Combine(temp.Options.BackupsDirectory, backupFileName);

        // Mutate live DB
        await using (var db = factory.CreateDbContext())
        {
            var shop = await db.Shops.FirstAsync();
            shop.Name = "Changed Before Stream Restore";
            await db.SaveChangesAsync();
        }

        // Restore via Stream
        await using (var fs = File.OpenRead(backupPath))
        {
            var result = await service.RestoreBackupFromStreamAsync(fs, backupFileName, user: "stream-admin");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.PreRestoreSafetyBackup);
        }

        // Verify live DB was restored
        await using (var db = factory.CreateDbContext())
        {
            var shop = await db.Shops.FirstAsync();
            Assert.Equal("Stream Shop", shop.Name);
        }
    }

    [Fact]
    public async Task RestoreBackup_aborts_and_leaves_live_state_intact_if_validation_fails()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 777, EtsyUserId = 888, Name = "Untouched Shop", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Create an export zip (which is not a full_restore backup)
        var exportName = await service.CreateListingExportAsync();

        // Attempt restore of export zip
        var result = await service.RestoreBackupAsync(exportName, user: "bad-actor");
        Assert.False(result.Success);
        Assert.Contains("Only 'full_restore' archives can be restored", result.ErrorMessage!);

        // Verify live DB is completely untouched
        await using (var db = factory.CreateDbContext())
        {
            var shop = await db.Shops.FirstAsync();
            Assert.Equal("Untouched Shop", shop.Name);
        }

        // Verify no backup.restored audit entry was written
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.restored");
    }

    [Fact]
    public async Task RestoreBackup_does_not_delete_target_archive_when_max_count_is_reached()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        // Precondition: Set MaxCount = 3, retention days = 365 (to test maxCount constraint exclusively)
        const int maxCount = 3;
        await settings.SetAsync(SettingKeys.BackupsMaxCount, maxCount.ToString());
        await settings.SetAsync(SettingKeys.BackupsRetentionDays, "365");

        var baseTime = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = baseTime };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // 1. Seed initial database state for Backup 1 (the oldest backup, our target to restore)
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 1001,
                EtsyUserId = 2001,
                Name = "Oldest Target Shop 1",
                CurrencyCode = "USD",
                ConnectedAtUtc = baseTime.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        // Create Backup 1 (T0)
        var b1FileName = await service.CreateRestorableBackupAsync(user: "setup-b1");
        var b1Path = Path.Combine(temp.Options.BackupsDirectory, b1FileName);
        Assert.True(File.Exists(b1Path));

        // Create Backup 2 (T1 = +1h)
        time.Now = baseTime.AddHours(1);
        await using (var db = factory.CreateDbContext())
        {
            var s = await db.Shops.FirstAsync(s => s.Id == 1);
            s.Name = "Shop 1 Mutated At T1";
            await db.SaveChangesAsync();
        }
        var b2FileName = await service.CreateRestorableBackupAsync(user: "setup-b2");
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b2FileName)));

        // Create Backup 3 (T2 = +2h) -> Reaching exactly MaxCount (3) backups
        time.Now = baseTime.AddHours(2);
        await using (var db = factory.CreateDbContext())
        {
            var s = await db.Shops.FirstAsync(s => s.Id == 1);
            s.Name = "Shop 1 Mutated At T2";
            await db.SaveChangesAsync();
        }
        var b3FileName = await service.CreateRestorableBackupAsync(user: "setup-b3");
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b3FileName)));

        // Verify Precondition: Exactly MaxCount (3) restorable backups exist on disk
        var initialBackups = service.ListBackups().Where(b => b.BackupType == "Restorable").ToList();
        Assert.Equal(maxCount, initialBackups.Count);
        Assert.Equal(b1FileName, initialBackups.Last().FileName); // b1 is the oldest

        // 2. Mutate live state before restore (T3 = +3h)
        time.Now = baseTime.AddHours(3);
        await using (var db = factory.CreateDbContext())
        {
            var s = await db.Shops.FirstAsync(s => s.Id == 1);
            s.Name = "Live Shop Mutated Right Before Restore";
            db.Shops.Add(new Shop
            {
                Id = 2,
                EtsyShopId = 1002,
                EtsyUserId = 2002,
                Name = "Temporary Live Shop 2",
                CurrencyCode = "USD",
                ConnectedAtUtc = time.Now.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        // 3. Action: Invoke RestoreBackupAsync on the oldest backup (b1)
        var restoreResult = await service.RestoreBackupAsync(b1FileName, user: "admin");

        // 4. Assertion 1: The target backup is NOT deleted during safety backup creation
        Assert.True(File.Exists(b1Path),
            $"Target backup archive '{b1FileName}' must NOT be deleted during safety backup creation or retention pruning.");

        // 5. Assertion 2: Restore succeeds with Success == true
        Assert.True(restoreResult.Success, $"Restore failed with error: {restoreResult.ErrorMessage}");
        Assert.Null(restoreResult.ErrorMessage);

        // Verify live database state strictly matches Backup 1
        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.OrderBy(s => s.Id).ToListAsync();
            Assert.Single(shops);
            Assert.Equal(1, shops[0].Id);
            Assert.Equal("Oldest Target Shop 1", shops[0].Name);
        }

        // 6. Assertion 3: Pre-restore safety backup exists on disk
        Assert.NotNull(restoreResult.PreRestoreSafetyBackup);
        var safetyBackupPath = Path.Combine(temp.Options.BackupsDirectory, restoreResult.PreRestoreSafetyBackup);
        Assert.True(File.Exists(safetyBackupPath),
            $"Pre-restore safety backup '{restoreResult.PreRestoreSafetyBackup}' must exist on disk.");
        Assert.NotEqual(b1FileName, restoreResult.PreRestoreSafetyBackup);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("  \0  ")]
    [InlineData("C:\\PathWith\0NullChar")]
    [InlineData("%TEMP%\\esm_probe\0sub")]
    [InlineData("C:\\invalid|pipe")]
    [InlineData("C:\\invalid<angle>dir")]
    [InlineData("C:\\invalid\"quote")]
    [InlineData("C:\\invalid*wildcard")]
    [InlineData("C:\\invalid?wildcard")]
    [InlineData("C:\\invalid\u0001control")]
    [InlineData("C:\\invalid\u001Fcontrol")]
    public void ProbeBackupDirectory_rejects_null_bytes_and_invalid_path_characters(string invalidPath)
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

        var result = service.ProbeBackupDirectory(invalidPath);

        Assert.False(result.IsValid, $"Probe should reject path with invalid characters: '{invalidPath}' (IsValid was true)");
        Assert.False(result.Success, $"Probe should reject path with invalid characters: '{invalidPath}' (Success was true)");
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("invalid", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeBackupDirectory_prevents_truncated_directory_creation_on_null_bytes()
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

        var testBaseDir = Path.Combine(Path.GetTempPath(), "esm-probe-null-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testBaseDir);
            var truncatedTarget = Path.Combine(testBaseDir, "truncated_target_dir");
            var poisonPath = truncatedTarget + "\0poison_suffix";

            var probeResult = service.ProbeBackupDirectory(poisonPath);

            Assert.False(probeResult.IsValid);
            Assert.False(probeResult.Success);
            Assert.NotNull(probeResult.ErrorMessage);
            Assert.Contains("invalid", probeResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            Assert.False(Directory.Exists(truncatedTarget),
                "Probe must NOT truncate path at null byte and create folder on disk.");
        }
        finally
        {
            try { if (Directory.Exists(testBaseDir)) Directory.Delete(testBaseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PruneBackupsAsync_respects_exclude_file_name()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var time = new ImmediateTimeProvider { Now = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero) };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Create 3 backups without pruning
        var b1 = await service.CreateRestorableBackupAsync(user: "b1", prune: false);
        time.Now = time.Now.AddMinutes(10);
        var b2 = await service.CreateRestorableBackupAsync(user: "b2", prune: false);
        time.Now = time.Now.AddMinutes(10);
        var b3 = await service.CreateRestorableBackupAsync(user: "b3", prune: false);

        // Prune with maxCount: 1, excluding b1 (the oldest file)
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 1, excludeFileName: b1);

        // b3 is newest (kept by invariant).
        // b1 is oldest but excluded (kept).
        // b2 is candidate (pruned).
        Assert.Equal(1, pruned);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b3)));
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b1)));
        Assert.False(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b2)));
    }

    [Fact]
    public async Task PruneBackupsAsync_respects_exclude_file_names_collection()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var time = new ImmediateTimeProvider { Now = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero) };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var b1 = await service.CreateRestorableBackupAsync(user: "b1", prune: false);
        time.Now = time.Now.AddMinutes(10);
        var b2 = await service.CreateRestorableBackupAsync(user: "b2", prune: false);
        time.Now = time.Now.AddMinutes(10);
        var b3 = await service.CreateRestorableBackupAsync(user: "b3", prune: false);
        time.Now = time.Now.AddMinutes(10);
        var b4 = await service.CreateRestorableBackupAsync(user: "b4", prune: false);

        // Prune with maxCount: 1, excluding b1 and b2
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 1, excludeFileNames: [b1, b2]);

        // b4 is newest (kept).
        // b1 and b2 are excluded (kept).
        // b3 is pruned.
        Assert.Equal(1, pruned);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b4)));
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b1)));
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b2)));
        Assert.False(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b3)));
    }
}
