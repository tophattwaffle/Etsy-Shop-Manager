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
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical adversarial stress tests for Milestone M9 (Backup Restoration & Configurable Location).
/// Validates boundary conditions, security traversal defenses, corruption handling, rollback safety,
/// and same-second collision prevention.
/// </summary>
public class BackupAdversarialChallengeTests
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

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    // =========================================================================
    // 1. CONFIGURABLE DIRECTORY EDGE CASES
    // =========================================================================

    [Theory]
    [InlineData("Backups With Spaces")]
    [InlineData("Special (Chars) & #1 [2026]")]
    [InlineData("Dropbox/Subfolder/Backups")]
    [InlineData("OneDrive - Personal/Backups")]
    public async Task Configurable_directory_supports_spaces_special_chars_and_cloud_sync_folders(string relativeFolder)
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 1001, EtsyUserId = 2001, Name = "Folder Edge Case Shop", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        var customPath = Path.Combine(temp.Options.ResolvedRoot, relativeFolder);

        // 1. Probe the directory
        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var probeResult = service.ProbeBackupDirectory(customPath);
        Assert.True(probeResult.Success, probeResult.ErrorMessage);
        Assert.Equal(Path.GetFullPath(customPath), probeResult.ResolvedPath);

        // 2. Configure directory setting
        await settings.SetAsync(SettingKeys.BackupsDirectory, customPath);

        // 3. Verify GetBackupsDirectoryAsync resolves to the custom path
        var resolvedDir = await service.GetBackupsDirectoryAsync();
        Assert.Equal(Path.GetFullPath(customPath), resolvedDir);
        Assert.True(Directory.Exists(resolvedDir));

        // 4. Create a backup in this directory
        var backupFileName = await service.CreateRestorableBackupAsync(user: "adversarial-tester");
        var backupFullPath = Path.Combine(resolvedDir, backupFileName);
        Assert.True(File.Exists(backupFullPath), $"Expected backup to be created at {backupFullPath}");

        // 5. List backups and verify it's enumerated from the custom directory
        var list = service.ListBackups();
        Assert.Contains(list, b => b.FileName == backupFileName);

        // 6. Verify retention pruning works in the custom directory
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 1, user: "tester");
        Assert.Equal(0, pruned); // Preserves single backup

        // 7. Restore from the custom directory
        var restoreResult = await service.RestoreBackupAsync(backupFileName, user: "tester");
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.True(settings.ClearCacheCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Blank_or_whitespace_directory_strictly_falls_back_to_default_storage_backups_directory(string? blankPath)
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await settings.SetAsync(SettingKeys.BackupsDirectory, blankPath);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        var probe = service.ProbeBackupDirectory(blankPath);
        Assert.True(probe.Success);
        Assert.Equal(temp.Options.BackupsDirectory, probe.ResolvedPath);

        var resolved = await service.GetBackupsDirectoryAsync();
        Assert.Equal(temp.Options.BackupsDirectory, resolved);

        var syncResolved = service.GetBackupsDirectory();
        Assert.Equal(temp.Options.BackupsDirectory, syncResolved);
    }

    [Fact]
    public void Invalid_path_syntax_and_unwritable_directory_probe_fail_gracefully()
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

        // 1. Path with null character causes ExpandEnvironmentVariables to return empty path, throwing in Path.GetFullPath
        var syntaxProbe = service.ProbeBackupDirectory("\0");
        Assert.False(syntaxProbe.Success);
        Assert.NotNull(syntaxProbe.ErrorMessage);
        Assert.Contains("Invalid path syntax", syntaxProbe.ErrorMessage);

        // 2. Non-existent unmapped drive fails Directory.CreateDirectory
        var unwritableProbe = service.ProbeBackupDirectory("Z:\\NonExistentDriveLetter\\BackupDir");
        Assert.False(unwritableProbe.Success);
        Assert.NotNull(unwritableProbe.ErrorMessage);
        Assert.Contains("Directory is not writable", unwritableProbe.ErrorMessage);
    }

    // =========================================================================
    // 2. MALFORMED ARCHIVES & SECURITY DEFENSE
    // =========================================================================

    [Fact]
    public async Task Malformed_archive_with_corrupt_zip_header_is_rejected()
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

        var corruptFile = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-010101.zip");
        await File.WriteAllBytesAsync(corruptFile, [0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

        var result = await service.ValidateBackupArchiveAsync(corruptFile);
        Assert.False(result.IsValid);
        Assert.Contains("Corrupted or invalid backup archive", result.ErrorMessage!);

        // Also test stream validation
        await using var stream = File.OpenRead(corruptFile);
        var streamResult = await service.ValidateBackupStreamAsync(stream);
        Assert.False(streamResult.IsValid);
        Assert.Contains("Corrupted or invalid backup archive", streamResult.ErrorMessage!);
    }

    [Fact]
    public async Task Malformed_archive_missing_manifest_is_rejected()
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-020202.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("etsyshopmanager.db");
            using var w = new StreamWriter(entry.Open());
            w.Write("dummy");
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.Contains("Archive is missing manifest.json", result.ErrorMessage!);
    }

    [Fact]
    public async Task Malformed_archive_with_listings_export_manifest_is_rejected()
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-030303.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using var w = new StreamWriter(manifestEntry.Open());
            w.Write(JsonSerializer.Serialize(new
            {
                version = 1,
                backup_type = "listings_export",
                shop_count = 1
            }));
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.Contains("Only 'full_restore' archives can be restored", result.ErrorMessage!);
    }

    [Fact]
    public async Task Malformed_archive_missing_database_is_rejected()
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-040404.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using var w = new StreamWriter(manifestEntry.Open());
            w.Write(JsonSerializer.Serialize(new
            {
                version = 1,
                backup_type = "full_restore",
                database_size_bytes = 100
            }));
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.Contains("Archive is missing etsyshopmanager.db", result.ErrorMessage!);
    }

    [Fact]
    public async Task Malformed_archive_with_zero_byte_database_is_rejected()
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-050505.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifestEntry.Open()))
            {
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
            }
            // Create 0-byte db entry
            zip.CreateEntry("etsyshopmanager.db");
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.Contains("Database file in archive is empty", result.ErrorMessage!);
    }

    [Fact]
    public async Task Malformed_archive_with_corrupt_sqlite_payload_is_rejected()
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-060606.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifestEntry.Open()))
            {
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
            }
            var dbEntry = zip.CreateEntry("etsyshopmanager.db");
            using (var w = new StreamWriter(dbEntry.Open()))
            {
                // Non-empty, but invalid SQLite header
                w.Write("THIS IS NOT A VALID SQLITE DATABASE FILE HEADER!!");
            }
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.True(result.ErrorMessage!.Contains("SQLite") || result.ErrorMessage.Contains("Corrupted or invalid"),
            $"Expected SQLite corruption error, got: {result.ErrorMessage}");
    }

    [Fact]
    public async Task Malformed_archive_missing_required_app_tables_is_rejected()
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

        // Create a valid SQLite db with an unrelated table (Pooling=False so file lock is released immediately)
        var dummyDbPath = Path.Combine(temp.Options.BackupsDirectory, "dummy.db");
        await using (var conn = new SqliteConnection($"Data Source={dummyDbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE OtherTable (Id INT); INSERT INTO OtherTable VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
            conn.Close();
            SqliteConnection.ClearPool(conn);
        }

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-070707.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifestEntry.Open()))
            {
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
            }
            zip.CreateEntryFromFile(dummyDbPath, "etsyshopmanager.db");
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.Contains("missing essential application tables", result.ErrorMessage!);
    }

    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("images/../../outside.txt")]
    [InlineData("keys/../escaped.txt")]
    [InlineData("/root/secret.txt")]
    [InlineData("\\windows\\win.ini")]
    [InlineData("C:/evil.txt")]
    [InlineData("entry:stream")]
    [InlineData("payload.sh")]
    [InlineData("unauthorized_dir/hack.dll")]
    public async Task Adversarial_zip_entries_with_traversal_or_unauthorized_paths_are_strictly_rejected(string maliciousEntry)
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

        var zipPath = Path.Combine(temp.Options.BackupsDirectory, "backup-20261022-080808.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            using (var w = new StreamWriter(manifestEntry.Open()))
            {
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
            }
            zip.CreateEntry("etsyshopmanager.db");
            zip.CreateEntry(maliciousEntry);
        }

        var result = await service.ValidateBackupArchiveAsync(zipPath);
        Assert.False(result.IsValid);
        Assert.True(result.ErrorMessage!.Contains("malicious entry path") || result.ErrorMessage.Contains("unauthorized entry"),
            $"Expected traversal or unauthorized entry message, got: {result.ErrorMessage}");
    }

    // =========================================================================
    // 3. DATA SAFETY INVARIANT: INVALID ARCHIVES NEVER MODIFY LIVE STATE
    // =========================================================================

    [Fact]
    public async Task Invalid_archives_are_rejected_without_modifying_live_database_media_or_keys()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        // 1. Establish pristine live state
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop
            {
                Id = 1,
                EtsyShopId = 77777,
                EtsyUserId = 88888,
                Name = "Pristine Live Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var liveImgDir = Path.Combine(temp.Options.ImagesDirectory, "pristine");
        Directory.CreateDirectory(liveImgDir);
        var liveImgFile = Path.Combine(liveImgDir, "live-image.jpg");
        var originalImgBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await File.WriteAllBytesAsync(liveImgFile, originalImgBytes);

        var liveVidDir = Path.Combine(temp.Options.VideosDirectory, "pristine");
        Directory.CreateDirectory(liveVidDir);
        var liveVidFile = Path.Combine(liveVidDir, "live-video.mp4");
        var originalVidBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        await File.WriteAllBytesAsync(liveVidFile, originalVidBytes);

        var liveKeyFile = Path.Combine(temp.Options.KeysDirectory, "live-key.xml");
        const string originalKeyText = "<key id=\"live\"><value>super-secret</value></key>";
        await File.WriteAllTextAsync(liveKeyFile, originalKeyText);

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        // Create adversarial attacks
        var attacks = new List<(string Name, Action<string> Creator)>
        {
            ("corrupt-zip", path => File.WriteAllBytes(path, [0x00, 0x01, 0x02])),
            ("missing-manifest", path =>
            {
                using var z = ZipFile.Open(path, ZipArchiveMode.Create);
                z.CreateEntry("dummy.txt");
            }),
            ("listings-export", path =>
            {
                using var z = ZipFile.Open(path, ZipArchiveMode.Create);
                var me = z.CreateEntry("manifest.json");
                using var w = new StreamWriter(me.Open());
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "listings_export" }));
            }),
            ("missing-db", path =>
            {
                using var z = ZipFile.Open(path, ZipArchiveMode.Create);
                var me = z.CreateEntry("manifest.json");
                using var w = new StreamWriter(me.Open());
                w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
            }),
            ("corrupt-db", path =>
            {
                using var z = ZipFile.Open(path, ZipArchiveMode.Create);
                var me = z.CreateEntry("manifest.json");
                using (var w = new StreamWriter(me.Open()))
                {
                    w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
                }
                var de = z.CreateEntry("etsyshopmanager.db");
                using (var w = new StreamWriter(de.Open()))
                {
                    w.Write("GARBAGE NOT SQLITE");
                }
            }),
            ("zip-slip-attack", path =>
            {
                using var z = ZipFile.Open(path, ZipArchiveMode.Create);
                var me = z.CreateEntry("manifest.json");
                using (var w = new StreamWriter(me.Open()))
                {
                    w.Write(JsonSerializer.Serialize(new { version = 1, backup_type = "full_restore" }));
                }
                z.CreateEntry("../../escaped.txt");
            })
        };

        var attackCounter = 10;
        foreach (var (attackName, creator) in attacks)
        {
            var fileName = $"backup-20261022-09{attackCounter++:D2}00.zip";
            var filePath = Path.Combine(temp.Options.BackupsDirectory, fileName);
            creator(filePath);

            // Attempt restore via filename
            var result = await service.RestoreBackupAsync(fileName, user: $"attacker-{attackName}");
            Assert.False(result.Success, $"Expected attack '{attackName}' to be rejected.");
            Assert.NotNull(result.ErrorMessage);

            // Attempt restore via stream
            await using (var fs = File.OpenRead(filePath))
            {
                var streamResult = await service.RestoreBackupFromStreamAsync(fs, fileName, user: $"attacker-{attackName}");
                Assert.False(streamResult.Success, $"Expected stream attack '{attackName}' to be rejected.");
            }
        }

        // Verify live state remains 100% UNTOUCHED
        await using (var db = factory.CreateDbContext())
        {
            var shops = await db.Shops.ToListAsync();
            Assert.Single(shops);
            Assert.Equal(77777, shops[0].EtsyShopId);
            Assert.Equal("Pristine Live Shop", shops[0].Name);
        }

        Assert.Equal(originalImgBytes, await File.ReadAllBytesAsync(liveImgFile));
        Assert.Equal(originalVidBytes, await File.ReadAllBytesAsync(liveVidFile));
        Assert.Equal(originalKeyText, await File.ReadAllTextAsync(liveKeyFile));

        // Ensure no escaped.txt was written anywhere
        Assert.False(File.Exists(Path.Combine(temp.Options.ResolvedRoot, "..", "escaped.txt")));

        // Ensure no 'backup.restored' audit entries exist
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.restored");
    }

    // =========================================================================
    // 4. SAME-SECOND BACKUP & RESTORE FILENAME COLLISION AVOIDANCE
    // =========================================================================

    [Fact]
    public async Task Same_second_backup_and_restore_avoids_filename_collisions()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 501, EtsyUserId = 601, Name = "Time Freeze Shop", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        // Freeze time provider to exact second
        var frozenTime = new DateTimeOffset(2026, 10, 22, 14, 30, 00, TimeSpan.Zero);
        var time = new ImmediateTimeProvider { Now = frozenTime };

        var service = new BackupService(
            temp.Options,
            factory,
            settings,
            audit,
            time,
            NullLogger<BackupService>.Instance);

        // 1. Create Backup 1 at 14:30:00
        var b1 = await service.CreateRestorableBackupAsync(user: "b1");
        Assert.Equal("backup-20261022-143000.zip", b1);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b1)));

        // 2. Create Backup 2 at SAME SECOND 14:30:00
        var b2 = await service.CreateRestorableBackupAsync(user: "b2");
        Assert.Equal("backup-20261022-143000-1.zip", b2);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b2)));

        // 3. Create Backup 3 at SAME SECOND 14:30:00
        var b3 = await service.CreateRestorableBackupAsync(user: "b3");
        Assert.Equal("backup-20261022-143000-2.zip", b3);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b3)));

        // Verify all 3 are distinct and listed by ListBackups()
        var list = service.ListBackups();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, b => b.FileName == b1);
        Assert.Contains(list, b => b.FileName == b2);
        Assert.Contains(list, b => b.FileName == b3);

        // 4. In the SAME SECOND, restore Backup 1
        // The restore must create a pre-restore safety backup, which must NOT collide with b1, b2, or b3.
        var restoreResult = await service.RestoreBackupAsync(b1, user: "admin");
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.NotNull(restoreResult.PreRestoreSafetyBackup);

        // Safety backup must be the next counter (-3)
        Assert.Equal("backup-20261022-143000-3.zip", restoreResult.PreRestoreSafetyBackup);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, restoreResult.PreRestoreSafetyBackup)));

        // Verify b1 was NOT overwritten or deleted
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, b1)));

        // 5. Test same-second export collisions
        var exp1 = await service.CreateListingExportAsync(user: "exp1");
        Assert.Equal("etsy-listings-export-20261022-143000.zip", exp1);

        var exp2 = await service.CreateListingExportAsync(user: "exp2");
        Assert.Equal("etsy-listings-export-20261022-143000-1.zip", exp2);

        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, exp1)));
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, exp2)));
    }

    [Fact]
    public async Task Path_traversal_attempts_in_restore_and_delete_throw_argument_exceptions()
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

        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreBackupAsync("../../secret.zip"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreBackupAsync("..\\secret.zip"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreBackupAsync("C:/windows/system32/calc.zip"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreBackupAsync("malicious-unrecognized-name.zip"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteBackupAsync("../../secret.zip"));
        Assert.Throws<ArgumentException>(() => service.GetBackupPath("../../secret.zip"));
    }
}
