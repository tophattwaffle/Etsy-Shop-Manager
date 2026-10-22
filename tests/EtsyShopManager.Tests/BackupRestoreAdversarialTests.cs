using System.IO.Compression;
using System.Text.Json;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Backups;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class BackupRestoreAdversarialTests
{
    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? UserName)> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default)
        {
            lock (Entries)
            {
                Entries.Add((action, entityType, entityId, details, userName));
            }
            return Task.CompletedTask;
        }
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
    public async Task RestoreBackup_UnderActiveSQLiteConnections_DoesNotThrowWindowsFileLockIOException_AndSucceeds()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State: Shop 1, image, video, key
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 1001,
                EtsyUserId = 2001,
                Name = "Original Shop 1",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var origImgBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var imgSub = Path.Combine(temp.Options.ImagesDirectory, "orig");
        Directory.CreateDirectory(imgSub);
        var origImgFile = Path.Combine(imgSub, "img1.jpg");
        await File.WriteAllBytesAsync(origImgFile, origImgBytes);

        var origVidBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var vidSub = Path.Combine(temp.Options.VideosDirectory, "orig");
        Directory.CreateDirectory(vidSub);
        var origVidFile = Path.Combine(vidSub, "vid1.mp4");
        await File.WriteAllBytesAsync(origVidFile, origVidBytes);

        var origKeyFile = Path.Combine(temp.Options.KeysDirectory, "key1.xml");
        await File.WriteAllTextAsync(origKeyFile, "<key>orig-key</key>");

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Create Backup 1 (the target archive to restore)
        var backup1FileName = await service.CreateRestorableBackupAsync(user: "setup");
        var backup1Path = Path.Combine(temp.Options.BackupsDirectory, backup1FileName);
        Assert.True(File.Exists(backup1Path), "Backup 1 archive must exist.");

        // 2. Mutate live state: add Shop 2, modify Shop 1, mutate image
        await using (var db = factory.CreateDbContext())
        {
            var s1 = await db.Shops.FirstAsync(s => s.Id == 1);
            s1.Name = "Mutated Shop 1";
            db.Shops.Add(new Shop
            {
                Id = 2,
                EtsyShopId = 2002,
                EtsyUserId = 3002,
                Name = "Mutated Shop 2",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await File.WriteAllBytesAsync(origImgFile, [0x99, 0x99, 0x99]);

        // 3. Keep ACTIVE SQLite connections open holding handles on the database file
        await using var rawActiveConn = new SqliteConnection(temp.Options.ConnectionString);
        await rawActiveConn.OpenAsync();
        await using (var cmd = rawActiveConn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Shops;";
            var preCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(2, preCount);
        }

        // Active pooled DbContext holding an open connection
        await using var activeDbContext = factory.CreateDbContext();
        var preShops = await activeDbContext.Shops.ToListAsync();
        Assert.Equal(2, preShops.Count);

        // Concurrent background reader simulating ongoing web server traffic
        using var cts = new CancellationTokenSource();
        var backgroundReadErrors = 0;
        var backgroundTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await using var bgDb = factory.CreateDbContext();
                    _ = await bgDb.Shops.AsNoTracking().CountAsync(cts.Token);
                    await Task.Delay(5, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    Interlocked.Increment(ref backgroundReadErrors);
                }
            }
        });

        // 4. Perform restore under active open & pooled connections
        var restoreResult = await service.RestoreBackupAsync(backup1FileName, user: "admin-online");

        // Stop background reader
        cts.Cancel();
        await backgroundTask;

        // 5. Assert: No Windows file-locking IOException, restore succeeds
        Assert.True(restoreResult.Success, $"Restore failed with error: {restoreResult.ErrorMessage}");
        Assert.NotNull(restoreResult.PreRestoreSafetyBackup);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, restoreResult.PreRestoreSafetyBackup)));

        // 6. Assert: Live database reflects restored state
        await using (var freshDb = factory.CreateDbContext())
        {
            var shops = await freshDb.Shops.ToListAsync();
            Assert.Single(shops);
            Assert.Equal(1, shops[0].Id);
            Assert.Equal("Original Shop 1", shops[0].Name);
        }

        // 7. Assert: Restored media and keys
        Assert.Equal(origImgBytes, await File.ReadAllBytesAsync(origImgFile));

        // 8. Assert: SQLite PRAGMA integrity_check passes
        await using var verifyConn = new SqliteConnection(temp.Options.ConnectionString);
        await verifyConn.OpenAsync();
        await using var integrityCmd = verifyConn.CreateCommand();
        integrityCmd.CommandText = "PRAGMA integrity_check;";
        var integrity = (string?)await integrityCmd.ExecuteScalarAsync();
        Assert.Equal("ok", integrity);
    }

    [Fact]
    public async Task RestoreBackup_WithRealSettingsStore_InvalidatesInMemoryCache_AndRestoresValues()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State: Configure settings in DB
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await settings.SetAsync("App.Title", "Shop Manager v1");
        await settings.SetAsync(SettingKeys.EtsyKeystring, "Keystring-Original-12345", encrypt: true);
        await settings.SetAsync(SettingKeys.BackupsRetentionDays, "30");
        await settings.SetAsync("Setting.WillBePrunedInBackup", "ToPrune");

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Create Backup 1 (captures original settings)
        var backup1FileName = await service.CreateRestorableBackupAsync(user: "setup");
        var backup1Path = Path.Combine(temp.Options.BackupsDirectory, backup1FileName);
        Assert.True(File.Exists(backup1Path));

        // 2. Mutate settings in live state and PRIME the in-memory cache
        await settings.SetAsync("App.Title", "Mutated Title v2");
        await settings.SetAsync(SettingKeys.EtsyKeystring, "Keystring-Mutated-99999", encrypt: true);
        await settings.SetAsync(SettingKeys.BackupsRetentionDays, "90");
        await settings.SetAsync("Setting.BrandNewKey", "BrandNewLiveValue");

        // Verify that SettingsStore cache contains the mutated values
        Assert.Equal("Mutated Title v2", await settings.GetAsync("App.Title"));
        Assert.Equal("Keystring-Mutated-99999", await settings.GetAsync(SettingKeys.EtsyKeystring));
        Assert.Equal("90", await settings.GetAsync(SettingKeys.BackupsRetentionDays));
        Assert.Equal("BrandNewLiveValue", await settings.GetAsync("Setting.BrandNewKey"));

        // 3. Execute Restore of Backup 1
        var restoreResult = await service.RestoreBackupAsync(backup1FileName, user: "admin");
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);

        // 4. In-Memory Cache Invalidation Verification:
        // Stale in-memory cache must be evicted. GetAsync must return restored values from database.
        var restoredTitle = await settings.GetAsync("App.Title");
        Assert.Equal("Shop Manager v1", restoredTitle);

        var restoredKeystring = await settings.GetAsync(SettingKeys.EtsyKeystring);
        Assert.Equal("Keystring-Original-12345", restoredKeystring);

        var restoredRetention = await settings.GetAsync(SettingKeys.BackupsRetentionDays);
        Assert.Equal("30", restoredRetention);

        var restoredWillBePruned = await settings.GetAsync("Setting.WillBePrunedInBackup");
        Assert.Equal("ToPrune", restoredWillBePruned);

        // Key that did not exist in Backup 1 must return null (not the stale cached "BrandNewLiveValue")
        var nonExistentKey = await settings.GetAsync("Setting.BrandNewKey");
        Assert.Null(nonExistentKey);

        // 5. Subsequent mutations post-restore must update both DB and cache properly
        await settings.SetAsync("App.Title", "Shop Manager v3-PostRestore");
        Assert.Equal("Shop Manager v3-PostRestore", await settings.GetAsync("App.Title"));
    }

    [Fact]
    public async Task RestoreBackup_PreRestoreSafetyBackup_ContainsExactLiveState_AndCanBeRestored()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State S1: Shop 1, Image 1, Video 1, Key 1, Setting 1
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 101,
                EtsyUserId = 201,
                Name = "Initial Shop 1",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var imgDir = Path.Combine(temp.Options.ImagesDirectory, "sub");
        Directory.CreateDirectory(imgDir);
        var img1Path = Path.Combine(imgDir, "img1.jpg");
        await File.WriteAllBytesAsync(img1Path, [0x01, 0x02, 0x03]);

        var vidDir = Path.Combine(temp.Options.VideosDirectory, "sub");
        Directory.CreateDirectory(vidDir);
        var vid1Path = Path.Combine(vidDir, "vid1.mp4");
        await File.WriteAllBytesAsync(vid1Path, [0x10, 0x20, 0x30]);

        var key1Path = Path.Combine(temp.Options.KeysDirectory, "key1.xml");
        await File.WriteAllTextAsync(key1Path, "<key>key1-content</key>");

        await settings.SetAsync("ConfigKey", "InitialStateValue");

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Backup 1 (contains State S1)
        var backup1FileName = await service.CreateRestorableBackupAsync(user: "setup");
        var backup1Path = Path.Combine(temp.Options.BackupsDirectory, backup1FileName);
        Assert.True(File.Exists(backup1Path));

        // 2. Mutate live state to State S2 (this is the PRE-RESTORE live state):
        // - Shop 1 modified name to "Shop 1 Mutated"
        // - Shop 2 added: "Shop 2 Live"
        // - Image 1 modified to [0x99, 0x88, 0x77]
        // - Image 2 added with [0x55, 0x66]
        // - Video 2 added with [0x77, 0x88]
        // - Key 2 added with "<key>key2-content</key>"
        // - Setting "ConfigKey" = "PreRestoreLiveMarker"
        await using (var db = factory.CreateDbContext())
        {
            var s1 = await db.Shops.FirstAsync(s => s.Id == 1);
            s1.Name = "Shop 1 Mutated";
            db.Shops.Add(new Shop
            {
                Id = 2,
                EtsyShopId = 202,
                EtsyUserId = 302,
                Name = "Shop 2 Live",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var mutatedImg1Bytes = new byte[] { 0x99, 0x88, 0x77 };
        await File.WriteAllBytesAsync(img1Path, mutatedImg1Bytes);

        var newImg2Bytes = new byte[] { 0x55, 0x66 };
        var img2Path = Path.Combine(imgDir, "img2.jpg");
        await File.WriteAllBytesAsync(img2Path, newImg2Bytes);

        var newVid2Bytes = new byte[] { 0x77, 0x88 };
        var vid2Path = Path.Combine(vidDir, "vid2.mp4");
        await File.WriteAllBytesAsync(vid2Path, newVid2Bytes);

        var key2Path = Path.Combine(temp.Options.KeysDirectory, "key2.xml");
        await File.WriteAllTextAsync(key2Path, "<key>key2-content</key>");

        await settings.SetAsync("ConfigKey", "PreRestoreLiveMarker");

        // 3. Restore Backup 1 (which replaces live state S2 with S1)
        var restoreResult = await service.RestoreBackupAsync(backup1FileName, user: "admin");
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.NotNull(restoreResult.PreRestoreSafetyBackup);

        var safetyBackupPath = Path.Combine(temp.Options.BackupsDirectory, restoreResult.PreRestoreSafetyBackup);
        Assert.True(File.Exists(safetyBackupPath), "Pre-restore safety backup archive must exist on disk.");

        // 4. EMPIRICALLY VERIFY PRE-RESTORE SAFETY BACKUP ARCHIVE INTEGRITY:
        // A. Validate archive with ValidateBackupArchiveAsync
        var valResult = await service.ValidateBackupArchiveAsync(safetyBackupPath);
        Assert.True(valResult.IsValid, $"Safety backup archive validation failed: {valResult.ErrorMessage}");
        Assert.Equal("full_restore", valResult.BackupType);
        Assert.Equal(2, valResult.ImageCount);
        Assert.Equal(2, valResult.VideoCount);
        Assert.Equal(2, valResult.KeyCount);

        // B. Inspect internal contents of the safety backup zip
        using (var archive = ZipFile.OpenRead(safetyBackupPath))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);
            using var manifestDoc = await JsonDocument.ParseAsync(manifestEntry.Open());
            var root = manifestDoc.RootElement;
            Assert.Equal("full_restore", root.GetProperty("backup_type").GetString());
            Assert.Equal(2, root.GetProperty("image_count").GetInt32());
            Assert.Equal(2, root.GetProperty("video_count").GetInt32());
            Assert.Equal(2, root.GetProperty("key_count").GetInt32());

            // Extract etsyshopmanager.db from safety backup and check records
            var tempDb = Path.Combine(Path.GetTempPath(), $"esm-safety-verify-{Guid.NewGuid():N}.db");
            try
            {
                archive.GetEntry("etsyshopmanager.db")!.ExtractToFile(tempDb);
                await using var conn = new SqliteConnection($"Data Source={tempDb};Pooling=False");
                await conn.OpenAsync();

                // PRAGMA integrity_check
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", (string?)await checkCmd.ExecuteScalarAsync());

                // Verify exact State S2 rows exist in the safety backup database
                checkCmd.CommandText = "SELECT COUNT(*) FROM Shops;";
                Assert.Equal(2, Convert.ToInt32(await checkCmd.ExecuteScalarAsync()));

                checkCmd.CommandText = "SELECT Name FROM Shops WHERE Id = 1;";
                Assert.Equal("Shop 1 Mutated", (string?)await checkCmd.ExecuteScalarAsync());

                checkCmd.CommandText = "SELECT Name FROM Shops WHERE Id = 2;";
                Assert.Equal("Shop 2 Live", (string?)await checkCmd.ExecuteScalarAsync());

                checkCmd.CommandText = "SELECT Value FROM Settings WHERE Key = 'ConfigKey';";
                Assert.Equal("PreRestoreLiveMarker", (string?)await checkCmd.ExecuteScalarAsync());
            }
            finally
            {
                if (File.Exists(tempDb))
                {
                    try { File.Delete(tempDb); } catch { }
                }
            }

            // Verify media bytes inside safety backup archive
            var img1Entry = archive.GetEntry("images/sub/img1.jpg");
            Assert.NotNull(img1Entry);
            using var ms1 = new MemoryStream();
            await img1Entry.Open().CopyToAsync(ms1);
            Assert.Equal(mutatedImg1Bytes, ms1.ToArray());

            var img2Entry = archive.GetEntry("images/sub/img2.jpg");
            Assert.NotNull(img2Entry);
            using var ms2 = new MemoryStream();
            await img2Entry.Open().CopyToAsync(ms2);
            Assert.Equal(newImg2Bytes, ms2.ToArray());
        }

        // 5. Assert: Live state is currently S1
        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.ToListAsync();
            Assert.Single(shops);
            Assert.Equal("Initial Shop 1", shops[0].Name);
        }
        Assert.Equal("InitialStateValue", await settings.GetAsync("ConfigKey"));

        // 6. RESTORE THE PRE-RESTORE SAFETY BACKUP:
        // Verify that restoring the safety backup completely reverts live state back to S2!
        var revertResult = await service.RestoreBackupAsync(restoreResult.PreRestoreSafetyBackup, user: "admin-revert");
        Assert.True(revertResult.Success, revertResult.ErrorMessage);

        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.OrderBy(s => s.Id).ToListAsync();
            Assert.Equal(2, shops.Count);
            Assert.Equal("Shop 1 Mutated", shops[0].Name);
            Assert.Equal("Shop 2 Live", shops[1].Name);
        }

        Assert.Equal("PreRestoreLiveMarker", await settings.GetAsync("ConfigKey"));
        Assert.Equal(mutatedImg1Bytes, await File.ReadAllBytesAsync(img1Path));
    }

    [Fact]
    public async Task RestoreBackup_ArchiveValidationFailure_LeavesZeroLiveStateChanges_AndCreatesNoSafetyBackup()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 555,
                EtsyUserId = 666,
                Name = "Immutable Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var imgDir = Path.Combine(temp.Options.ImagesDirectory, "sub");
        Directory.CreateDirectory(imgDir);
        var liveImgPath = Path.Combine(imgDir, "live.jpg");
        var liveImgBytes = new byte[] { 0x11, 0x22, 0x33 };
        await File.WriteAllBytesAsync(liveImgPath, liveImgBytes);

        await settings.SetAsync("TestKey", "LiveValue");

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Record initial backup file count
        var initialBackupCount = Directory.EnumerateFiles(temp.Options.BackupsDirectory, "*.zip").Count();

        // 2. Prepare 8 adversarial archive variants that violate validation invariants
        var corruptArchives = new List<string>();

        // Variant 1: Corrupted zip (random non-zip bytes)
        var f1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010001.zip");
        await File.WriteAllBytesAsync(f1, [0x01, 0x02, 0x03, 0x04, 0x05]);
        corruptArchives.Add("backup-20261022-010001.zip");

        // Variant 2: Missing manifest.json
        var f2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010002.zip");
        using (var zip = ZipFile.Open(f2, ZipArchiveMode.Create))
        {
            zip.CreateEntry("etsyshopmanager.db");
        }
        corruptArchives.Add("backup-20261022-010002.zip");

        // Variant 3: Missing etsyshopmanager.db
        var f3 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010003.zip");
        using (var zip = ZipFile.Open(f3, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
        }
        corruptArchives.Add("backup-20261022-010003.zip");

        // Variant 4: backup_type != "full_restore"
        var f4 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010004.zip");
        using (var zip = ZipFile.Open(f4, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"listings_export\"}");
            }
            zip.CreateEntry("etsyshopmanager.db");
        }
        corruptArchives.Add("backup-20261022-010004.zip");

        // Variant 5: Path traversal entry
        var f5 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010005.zip");
        using (var zip = ZipFile.Open(f5, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
            zip.CreateEntry("../malicious.dll");
        }
        corruptArchives.Add("backup-20261022-010005.zip");

        // Variant 6: Unauthorized executable entry
        var f6 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010006.zip");
        using (var zip = ZipFile.Open(f6, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
            zip.CreateEntry("etsyshopmanager.db");
            zip.CreateEntry("malware.exe");
        }
        corruptArchives.Add("backup-20261022-010006.zip");

        // Variant 7: Corrupted SQLite database inside zip (invalid header)
        var f7 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010007.zip");
        using (var zip = ZipFile.Open(f7, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
            var dbEntry = zip.CreateEntry("etsyshopmanager.db");
            using (var s = dbEntry.Open())
            {
                s.Write("not a sqlite database file header"u8);
            }
        }
        corruptArchives.Add("backup-20261022-010007.zip");

        // Variant 8: Valid SQLite database but missing required tables (Shops, Listings, Settings)
        var foreignDb = Path.Combine(Path.GetTempPath(), $"esm-foreign-{Guid.NewGuid():N}.db");
        await using (var foreignConn = new SqliteConnection($"Data Source={foreignDb};Pooling=False"))
        {
            await foreignConn.OpenAsync();
            await using var cmd = foreignConn.CreateCommand();
            cmd.CommandText = "CREATE TABLE ForeignTable (Id INT); INSERT INTO ForeignTable VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        }
        var f8 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010008.zip");
        using (var zip = ZipFile.Open(f8, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("{\"version\":1,\"backup_type\":\"full_restore\"}");
            }
            zip.CreateEntryFromFile(foreignDb, "etsyshopmanager.db");
        }
        File.Delete(foreignDb);
        corruptArchives.Add("backup-20261022-010008.zip");

        // 3. Test EACH corrupt archive: assert restore fails, zero live state modified, zero safety backups created
        foreach (var corruptFile in corruptArchives)
        {
            var countBefore = Directory.EnumerateFiles(temp.Options.BackupsDirectory, "*.zip").Count();

            var result = await service.RestoreBackupAsync(corruptFile, user: "attacker");
            Assert.False(result.Success, $"Expected restore to fail for {corruptFile}");
            Assert.NotNull(result.ErrorMessage);
            Assert.Null(result.PreRestoreSafetyBackup);

            // Assert zero safety backup archives were created
            var countAfter = Directory.EnumerateFiles(temp.Options.BackupsDirectory, "*.zip").Count();
            Assert.Equal(countBefore, countAfter);

            // Assert live DB is completely untouched
            await using (var db = factory.CreateDbContext())
            {
                var shops = await db.Shops.ToListAsync();
                Assert.Single(shops);
                Assert.Equal("Immutable Shop", shops[0].Name);
            }

            // Assert live image and setting are untouched
            Assert.Equal(liveImgBytes, await File.ReadAllBytesAsync(liveImgPath));
            Assert.Equal("LiveValue", await settings.GetAsync("TestKey"));
        }

        // 4. Assert zero backup.restored audit logs were emitted
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.restored");
    }

    [Fact]
    public async Task RestoreBackup_WhenSafetyBackupCreationFails_AbortsWithZeroLiveStateChanges()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 999,
                EtsyUserId = 888,
                Name = "Untouchable Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var liveImgPath = Path.Combine(temp.Options.ImagesDirectory, "sample.jpg");
        var liveImgBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await File.WriteAllBytesAsync(liveImgPath, liveImgBytes);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Create a valid archive to restore
        var validBackupFileName = await service.CreateRestorableBackupAsync(user: "setup");
        var validBackupPath = Path.Combine(temp.Options.BackupsDirectory, validBackupFileName);
        Assert.True(File.Exists(validBackupPath));

        // 2. Configure an unreachable backup directory (non-existent drive letter)
        // This will cause CreateRestorableBackupAsync to fail when trying to create directory
        await settings.SetAsync(SettingKeys.BackupsDirectory, @"Z:\NonExistentDrive\BackupsPath");

        // 3. Attempt restore via stream (archive validation succeeds on temp file, but safety backup creation fails)
        await using var stream = File.OpenRead(validBackupPath);
        var restoreResult = await service.RestoreBackupFromStreamAsync(stream, "uploaded-valid.zip", user: "admin");

        // 4. Assert: Abort safety
        Assert.False(restoreResult.Success);
        Assert.Contains("Pre-restore safety backup failed", restoreResult.ErrorMessage!);
        Assert.Null(restoreResult.PreRestoreSafetyBackup);

        // 5. Assert: Live database was completely untouched
        // Reset configured directory back so we can query normally
        await settings.SetAsync(SettingKeys.BackupsDirectory, null);
        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.ToListAsync();
            Assert.Single(shops);
            Assert.Equal("Untouchable Shop", shops[0].Name);
        }

        // 6. Assert: Media files untouched
        Assert.Equal(liveImgBytes, await File.ReadAllBytesAsync(liveImgPath));

        // 7. Assert: No backup.restored audit log
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.restored");
    }

    [Fact]
    public async Task RestoreBackupFromStream_UnderActiveConnections_Succeeds()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new SettingsStore(factory, dataProtection, TimeProvider.System, NullLogger<SettingsStore>.Instance);

        // 1. Initial State: Shop 42
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 42,
                EtsyUserId = 43,
                Name = "Original Stream Shop",
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

        var backupFileName = await service.CreateRestorableBackupAsync(user: "setup");
        var backupPath = Path.Combine(temp.Options.BackupsDirectory, backupFileName);

        // Mutate live DB
        await using (var db = factory.CreateDbContext())
        {
            var s = await db.Shops.FirstAsync();
            s.Name = "Mutated Before Stream Restore";
            await db.SaveChangesAsync();
        }

        // Active connection open
        await using var activeConn = new SqliteConnection(temp.Options.ConnectionString);
        await activeConn.OpenAsync();

        // Restore via stream
        await using (var fs = File.OpenRead(backupPath))
        {
            var result = await service.RestoreBackupFromStreamAsync(fs, backupFileName, user: "stream-user");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.PreRestoreSafetyBackup);
        }

        // Verify live DB has restored value
        await using (var db = factory.CreateDbContext())
        {
            var s = await db.Shops.FirstAsync();
            Assert.Equal("Original Stream Shop", s.Name);
        }
    }
}
