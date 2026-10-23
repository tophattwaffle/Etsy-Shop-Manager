using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure.Backups;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class ChallengerM3RetentionJobTests
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
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private static readonly byte[] EmptyZipBytes = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public async Task Retention_pruning_zero_days_and_zero_max_count_prunes_nothing()
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

        // Create 6 backups spanning from 1 day to 180 days old
        var files = new[]
        {
            Path.Combine(temp.Options.BackupsDirectory, "backup-20260424-120000.zip"), // 180 days old
            Path.Combine(temp.Options.BackupsDirectory, "backup-20260723-120000.zip"), // 90 days old
            Path.Combine(temp.Options.BackupsDirectory, "backup-20260822-120000.zip"), // 60 days old
            Path.Combine(temp.Options.BackupsDirectory, "backup-20260921-120000.zip"), // 30 days old
            Path.Combine(temp.Options.BackupsDirectory, "backup-20261011-120000.zip"), // 10 days old
            Path.Combine(temp.Options.BackupsDirectory, "backup-20261020-120000.zip")  // 1 day old
        };

        foreach (var file in files)
        {
            await File.WriteAllBytesAsync(file, EmptyZipBytes);
        }

        // Test explicit parameters 0 days and 0 maxCount
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 0, user: "tester");

        Assert.Equal(0, pruned);
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"File {file} should not have been pruned when retention days and max count are 0.");
        }
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.pruned");

        // Test reading 0 days and 0 maxCount from settings store
        await settings.SetAsync(SettingKeys.BackupsRetentionDays, "0");
        await settings.SetAsync(SettingKeys.BackupsMaxCount, "0");

        var prunedViaSettings = await service.PruneBackupsAsync(retentionDays: null, maxCount: null, user: "tester");
        Assert.Equal(0, prunedViaSettings);
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"File {file} should not have been pruned via settings store values of 0.");
        }
    }

    [Theory]
    [InlineData(0, 1)] // 0 days (disabled age check), max count 1 -> keeps newest only
    [InlineData(1, 0)] // 1 day (all 5 are > 1 day old), max count 0 -> keeps newest only
    [InlineData(1, 1)] // 1 day, max count 1 -> keeps newest only
    public async Task Aggressive_retention_always_preserves_single_newest_backup_with_multiple_files(int days, int count)
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

        // 5 ancient backups, all > 5 days old
        var b1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20260901-120000.zip"); // 50 days old
        var b2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20260915-120000.zip"); // 36 days old
        var b3 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261001-120000.zip"); // 20 days old
        var b4 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261010-120000.zip"); // 11 days old
        var newest = Path.Combine(temp.Options.BackupsDirectory, "backup-20261015-120000.zip"); // 6 days old (newest)

        foreach (var file in new[] { b1, b2, b3, b4, newest })
        {
            await File.WriteAllBytesAsync(file, EmptyZipBytes);
        }

        var pruned = await service.PruneBackupsAsync(retentionDays: days, maxCount: count, user: "aggressive-tester");

        Assert.Equal(4, pruned);
        Assert.True(File.Exists(newest), "The single newest backup must ALWAYS be preserved regardless of aggressive settings!");
        Assert.False(File.Exists(b1), "Old backup b1 must be pruned.");
        Assert.False(File.Exists(b2), "Old backup b2 must be pruned.");
        Assert.False(File.Exists(b3), "Old backup b3 must be pruned.");
        Assert.False(File.Exists(b4), "Old backup b4 must be pruned.");

        Assert.Equal(4, audit.Entries.Count(e => e.Action == "backup.pruned"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 10)]
    public async Task Aggressive_retention_always_preserves_solitary_backup(int days, int count)
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

        // A single 10-year-old ancient backup
        var solitary = Path.Combine(temp.Options.BackupsDirectory, "backup-20160101-000000.zip");
        await File.WriteAllBytesAsync(solitary, EmptyZipBytes);

        var pruned = await service.PruneBackupsAsync(retentionDays: days, maxCount: count, user: "tester");

        Assert.Equal(0, pruned);
        Assert.True(File.Exists(solitary), "A single existing backup must NEVER be deleted by pruning under any settings!");
        Assert.DoesNotContain(audit.Entries, e => e.Action == "backup.pruned");
    }

    [Fact]
    public async Task Pruning_never_deletes_directories_or_non_backup_files()
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

        // 1. Create subdirectories in BackupsDirectory (e.g. deploy slots)
        var deploySlot1 = Path.Combine(temp.Options.BackupsDirectory, "deploy-20261021");
        var deploySlot2 = Path.Combine(temp.Options.BackupsDirectory, "deploy-staging");
        Directory.CreateDirectory(deploySlot1);
        Directory.CreateDirectory(deploySlot2);

        var slotFile1 = Path.Combine(deploySlot1, "etsyshopmanager.db");
        var slotFile2 = Path.Combine(deploySlot2, "appsettings.json");
        await File.WriteAllTextAsync(slotFile1, "dummy db content");
        await File.WriteAllTextAsync(slotFile2, "{}");

        // 2. Create custom and export files
        var customZip = Path.Combine(temp.Options.BackupsDirectory, "custom.zip");
        var notesTxt = Path.Combine(temp.Options.BackupsDirectory, "notes.txt");
        var exportZip = Path.Combine(temp.Options.BackupsDirectory, "etsy-listings-export-20261001-120000.zip");
        var tarGz = Path.Combine(temp.Options.BackupsDirectory, "archive.tar.gz");

        await File.WriteAllBytesAsync(customZip, EmptyZipBytes);
        await File.WriteAllTextAsync(notesTxt, "Important notes");
        await File.WriteAllBytesAsync(exportZip, EmptyZipBytes);
        await File.WriteAllBytesAsync(tarGz, [0x1F, 0x8B, 0x08]);

        // 3. Create regular backup files
        var b1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20260901-120000.zip");
        var b2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261021-120000.zip");
        await File.WriteAllBytesAsync(b1, EmptyZipBytes);
        await File.WriteAllBytesAsync(b2, EmptyZipBytes);

        // Run aggressive pruning: retention 1 day, max count 1
        var pruned = await service.PruneBackupsAsync(retentionDays: 1, maxCount: 1, user: "tester");

        Assert.Equal(1, pruned); // b1 is pruned

        // Verify subdirectories and their contents are 100% intact
        Assert.True(Directory.Exists(deploySlot1), "Directory deploy-20261021 must NOT be deleted.");
        Assert.True(Directory.Exists(deploySlot2), "Directory deploy-staging must NOT be deleted.");
        Assert.True(File.Exists(slotFile1), "Files inside deploy directory must NOT be deleted.");
        Assert.True(File.Exists(slotFile2), "Files inside deploy directory must NOT be deleted.");

        // Verify custom files are 100% intact
        Assert.True(File.Exists(customZip), "custom.zip must NOT be deleted by pruning.");
        Assert.True(File.Exists(notesTxt), "notes.txt must NOT be deleted by pruning.");
        Assert.True(File.Exists(exportZip), "Portable export zip must NOT be deleted by pruning.");
        Assert.True(File.Exists(tarGz), "archive.tar.gz must NOT be deleted by pruning.");

        // Verify backup files
        Assert.False(File.Exists(b1), "Old backup-20260901-120000.zip should be pruned.");
        Assert.True(File.Exists(b2), "Newest backup-20261021-120000.zip must be kept.");
    }

    [Fact]
    public async Task CreateBackupJob_executes_successfully_formats_result_and_creates_valid_archive()
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
                EtsyShopId = 112233,
                EtsyUserId = 445566,
                Name = "Job Shop",
                CurrencyCode = "USD",
                ConnectedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Add dummy key and image
        var keyFile = Path.Combine(temp.Options.KeysDirectory, "key-1.xml");
        await File.WriteAllTextAsync(keyFile, "<key>secret</key>");

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
            JobId = 555,
            JobType = CreateBackupJob.Type,
            Attempt = 1
        };

        // Execute job
        await job.HandleAsync(context, CancellationToken.None);

        // 1. Verify JobExecutionContext.Result formatting
        Assert.NotNull(context.Result);
        var match = Regex.Match(context.Result, @"^Created backup: (backup-\d{8}-\d{6}\.zip)$");
        Assert.True(match.Success, $"Result '{context.Result}' did not match expected format 'Created backup: backup-{{yyyyMMdd-HHmmss}}.zip'.");

        var fileName = match.Groups[1].Value;
        var backupPath = Path.Combine(temp.Options.BackupsDirectory, fileName);
        Assert.True(File.Exists(backupPath), $"Backup file '{backupPath}' must exist on disk.");

        // 2. Verify zip archive contents and sqlite validity
        using (var archive = ZipFile.OpenRead(backupPath))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);
            using (var s = manifestEntry.Open())
            {
                using var doc = JsonDocument.Parse(s);
                Assert.Equal("full_restore", doc.RootElement.GetProperty("backup_type").GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            }

            var dbEntry = archive.GetEntry("etsyshopmanager.db");
            Assert.NotNull(dbEntry);

            var extractedDb = Path.Combine(temp.Options.BackupsDirectory, $"extracted-{Guid.NewGuid():N}.db");
            try
            {
                dbEntry.ExtractToFile(extractedDb);
                await using (var conn = new SqliteConnection($"Data Source={extractedDb};Pooling=False"))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Name FROM Shops WHERE EtsyShopId = 112233;";
                    var shopName = (string?)await cmd.ExecuteScalarAsync();
                    Assert.Equal("Job Shop", shopName);
                    conn.Close();
                    SqliteConnection.ClearPool(conn);
                }
            }
            finally
            {
                if (File.Exists(extractedDb)) File.Delete(extractedDb);
            }

            Assert.NotNull(archive.GetEntry("keys/key-1.xml"));
        }

        // 3. Verify audit log entry contains user "scheduler"
        Assert.Contains(audit.Entries, e => e.Action == "backup.created" && e.UserName == "scheduler" && e.Details!.Contains(fileName));
    }

    [Fact]
    public async Task CreateBackupJob_automatically_prunes_older_backups_according_to_retention_settings()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new InMemorySettingsStore();
        var factory = new FileBasedTestDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Configure retention settings: max count = 2, retention days = 0 (count-based pruning only)
        await settings.SetAsync(SettingKeys.BackupsMaxCount, "2");
        await settings.SetAsync(SettingKeys.BackupsRetentionDays, "0");

        // Pre-create 3 older backups
        var old1 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261010-100000.zip");
        var old2 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261015-100000.zip");
        var old3 = Path.Combine(temp.Options.BackupsDirectory, "backup-20261018-100000.zip");
        await File.WriteAllBytesAsync(old1, EmptyZipBytes);
        await File.WriteAllBytesAsync(old2, EmptyZipBytes);
        await File.WriteAllBytesAsync(old3, EmptyZipBytes);

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
            JobId = 777,
            JobType = CreateBackupJob.Type,
            Attempt = 1
        };

        // When the job runs, it creates a new backup (the 4th).
        // Since MaxCount is 2, the two oldest (old1 and old2) should be pruned,
        // leaving old3 and the brand-new backup.
        await job.HandleAsync(context, CancellationToken.None);

        Assert.NotNull(context.Result);
        var backups = service.ListBackups();
        Assert.Equal(2, backups.Count);

        Assert.False(File.Exists(old1), "old1 must have been pruned by automatic retention.");
        Assert.False(File.Exists(old2), "old2 must have been pruned by automatic retention.");
        Assert.True(File.Exists(old3), "old3 (second newest) must remain.");

        var newest = backups.First(b => b.FileName.StartsWith("backup-2026"));
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, newest.FileName)));

        Assert.Equal(2, audit.Entries.Count(e => e.Action == "backup.pruned"));
    }
}
