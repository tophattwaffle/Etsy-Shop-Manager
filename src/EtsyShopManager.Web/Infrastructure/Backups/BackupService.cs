using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EtsyShopManager.Web.Infrastructure.Backups;

public sealed record BackupFileInfo(
    string FileName,
    string BackupType,
    long SizeBytes,
    double SizeMb,
    DateTime CreatedAtUtc);

public sealed record BackupValidationResult(
    bool IsValid,
    string? ErrorMessage = null,
    int Version = 0,
    string? BackupType = null,
    long DbSizeBytes = 0,
    int ImageCount = 0,
    int VideoCount = 0,
    int KeyCount = 0);

public sealed record BackupRestoreResult(
    bool Success,
    string? ErrorMessage = null,
    string? PreRestoreSafetyBackup = null,
    int RestoredImages = 0,
    int RestoredVideos = 0,
    int RestoredKeys = 0);

public sealed record BackupDirectoryProbeResult(
    bool Success,
    string? ResolvedPath = null,
    string? ErrorMessage = null)
{
    public bool IsValid => Success;
}

/// <summary>
/// Manages SQLite consistent snapshots, full restorable backup archives, retention pruning,
/// and portable listing exports.
/// </summary>
public sealed partial class BackupService(
    StorageOptions storage,
    IDbContextFactory<AppDbContext> dbFactory,
    ISettingsStore settings,
    IAuditLog audit,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const int DefaultScheduleIntervalHours = 24;
    public const int DefaultRetentionDays = 30;
    public const int DefaultMaxCount = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^backup-(\d{8}-\d{6})(?:-\d+)?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupFileNameRegex();

    [GeneratedRegex(@"^etsy-listings-export-(\d{8}-\d{6})(?:-\d+)?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportFileNameRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

    /// <summary>Resolves the configured backup directory or falls back to <see cref="StorageOptions.BackupsDirectory"/>.</summary>
    public async Task<string> GetBackupsDirectoryAsync(CancellationToken ct = default)
    {
        var configured = await settings.GetAsync(SettingKeys.BackupsDirectory, ct);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            Directory.CreateDirectory(resolved);
            return resolved;
        }
        storage.EnsureDirectories();
        return storage.BackupsDirectory;
    }

    /// <summary>Synchronous version of <see cref="GetBackupsDirectoryAsync"/>.</summary>
    public string GetBackupsDirectory()
    {
        var configured = settings.GetAsync(SettingKeys.BackupsDirectory).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
            Directory.CreateDirectory(resolved);
            return resolved;
        }
        storage.EnsureDirectories();
        return storage.BackupsDirectory;
    }

    /// <summary>
    /// Verifies path syntax and tests directory writeability via a temporary probe file.
    /// Blank/null path tests the default backups directory.
    /// </summary>
    public BackupDirectoryProbeResult ProbeBackupDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            try
            {
                storage.EnsureDirectories();
                var testFile = Path.Combine(storage.BackupsDirectory, $".probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "probe");
                File.Delete(testFile);
                return new BackupDirectoryProbeResult(true, storage.BackupsDirectory);
            }
            catch (Exception ex)
            {
                return new BackupDirectoryProbeResult(false, storage.BackupsDirectory, $"Default backup directory is not writable: {ex.Message}");
            }
        }

        var trimmed = directoryPath.Trim();
        if (trimmed.Contains('\0') ||
            trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            trimmed.IndexOfAny(['*', '?']) >= 0)
        {
            return new BackupDirectoryProbeResult(false, null, "Invalid path syntax: Directory path contains invalid characters.");
        }

        string resolved;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (expanded.Contains('\0') ||
                expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                expanded.IndexOfAny(['*', '?']) >= 0)
            {
                return new BackupDirectoryProbeResult(false, null, "Invalid path syntax: Directory path contains invalid characters.");
            }

            resolved = Path.GetFullPath(expanded);
        }
        catch (Exception ex)
        {
            return new BackupDirectoryProbeResult(false, null, $"Invalid path syntax: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(resolved);
            var testFile = Path.Combine(resolved, $".probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "probe");
            File.Delete(testFile);
            return new BackupDirectoryProbeResult(true, resolved);
        }
        catch (Exception ex)
        {
            return new BackupDirectoryProbeResult(false, resolved, $"Directory is not writable: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a consistent online snapshot of SQLite database using <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>
    /// without WAL page torn states or blocking readers/writers.
    /// </summary>
    public async Task<string> CreateDatabaseSnapshotAsync(
        string destinationDbPath,
        SqliteConnection? sourceConnection = null,
        CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        var destDir = Path.GetDirectoryName(destinationDbPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        if (File.Exists(destinationDbPath)) File.Delete(destinationDbPath);

        var destConnStr = $"Data Source={destinationDbPath};Pooling=False";
        if (sourceConnection is not null)
        {
            await using (var dest = new SqliteConnection(destConnStr))
            {
                await dest.OpenAsync(ct);
                await Task.Run(() => sourceConnection.BackupDatabase(dest), ct);
                dest.Close();
                SqliteConnection.ClearPool(dest);
            }
        }
        else
        {
            await using var source = new SqliteConnection(storage.ConnectionString);
            await source.OpenAsync(ct);

            await using (var dest = new SqliteConnection(destConnStr))
            {
                await dest.OpenAsync(ct);
                await Task.Run(() => source.BackupDatabase(dest), ct);
                dest.Close();
                SqliteConnection.ClearPool(dest);
            }
        }

        return destinationDbPath;
    }

    /// <summary>
    /// Creates a full restorable backup archive (.zip) containing:
    /// - manifest.json
    /// - etsyshopmanager.db (snapshot)
    /// - images/ (content-addressed store)
    /// - videos/
    /// - keys/ (Data Protection keys)
    /// Runs retention pruning automatically upon completion.
    /// </summary>
    public async Task<string> CreateRestorableBackupAsync(
        string? user = null,
        SqliteConnection? sourceConnection = null,
        bool prune = true,
        IEnumerable<string>? excludeFileNames = null,
        string? excludeFileName = null,
        CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        var backupsDir = await GetBackupsDirectoryAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var baseFileName = $"backup-{now:yyyyMMdd-HHmmss}";
        var fileName = $"{baseFileName}.zip";
        var finalZipPath = Path.Combine(backupsDir, fileName);
        if (File.Exists(finalZipPath))
        {
            var counter = 1;
            while (File.Exists(Path.Combine(backupsDir, $"{baseFileName}-{counter}.zip")))
            {
                counter++;
            }
            fileName = $"{baseFileName}-{counter}.zip";
            finalZipPath = Path.Combine(backupsDir, fileName);
        }
        var tempZipPath = Path.Combine(backupsDir, $"{fileName}.{Guid.NewGuid():N}.tmp");
        var tempDbPath = Path.Combine(storage.BackupsDirectory, $"snapshot-{Guid.NewGuid():N}.db");

        try
        {
            await CreateDatabaseSnapshotAsync(tempDbPath, sourceConnection, ct);

            var dbLength = new FileInfo(tempDbPath).Length;
            var imageFiles = Directory.Exists(storage.ImagesDirectory)
                ? Directory.EnumerateFiles(storage.ImagesDirectory, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            var videoFiles = Directory.Exists(storage.VideosDirectory)
                ? Directory.EnumerateFiles(storage.VideosDirectory, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            var keyFiles = Directory.Exists(storage.KeysDirectory)
                ? Directory.EnumerateFiles(storage.KeysDirectory, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];

            var manifest = new
            {
                version = 1,
                app_version = AppVersion.Version,
                created_at_utc = now,
                backup_type = "full_restore",
                database_size_bytes = dbLength,
                image_count = imageFiles.Count,
                video_count = videoFiles.Count,
                key_count = keyFiles.Count
            };

            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. manifest.json
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var entryStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, ct);
                }

                // 2. etsyshopmanager.db
                zip.CreateEntryFromFile(tempDbPath, "etsyshopmanager.db", CompressionLevel.Optimal);

                // 3. images/
                foreach (var img in imageFiles)
                {
                    var rel = Path.GetRelativePath(storage.ResolvedRoot, img).Replace('\\', '/');
                    zip.CreateEntryFromFile(img, rel, CompressionLevel.Fastest);
                }

                // 4. videos/
                foreach (var vid in videoFiles)
                {
                    var rel = Path.GetRelativePath(storage.ResolvedRoot, vid).Replace('\\', '/');
                    zip.CreateEntryFromFile(vid, rel, CompressionLevel.Fastest);
                }

                // 5. keys/
                foreach (var k in keyFiles)
                {
                    var rel = Path.GetRelativePath(storage.ResolvedRoot, k).Replace('\\', '/');
                    zip.CreateEntryFromFile(k, rel, CompressionLevel.Optimal);
                }
            }

            File.Move(tempZipPath, finalZipPath, overwrite: true);

            var sizeMb = new FileInfo(finalZipPath).Length / (1024.0 * 1024.0);
            await audit.WriteAsync("backup.created", "Backup", fileName,
                $"Created restorable backup {fileName} ({sizeMb:F2} MB)", user);
            logger.LogInformation("Created restorable backup {FileName} ({SizeMb:F2} MB)", fileName, sizeMb);

            // Automatic retention pruning
            if (prune)
            {
                await PruneBackupsAsync(
                    user: user ?? "system",
                    excludeFileNames: excludeFileNames,
                    excludeFileName: excludeFileName,
                    ct: ct);
            }

            return fileName;
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                try { File.Delete(tempDbPath); } catch { /* ignore */ }
            }
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Alias for <see cref="CreateRestorableBackupAsync"/>.</summary>
    public Task<string> CreateBackupAsync(string? label = null, CancellationToken ct = default) =>
        CreateRestorableBackupAsync(user: label, ct: ct);

    /// <summary>
    /// Retention pruning engine:
    /// Scans BackupsDirectory for 'backup-*.zip'.
    /// Invariant: NEVER deletes deploy-* folders or portable exports, and ALWAYS preserves the newest backup.
    /// Prunes older than retentionDays and beyond maxCount.
    /// Emits 'backup.pruned' audit log per pruned file.
    /// </summary>
    public async Task<int> PruneBackupsAsync(
        int? retentionDays = null,
        int? maxCount = null,
        string? user = "system",
        IEnumerable<string>? excludeFileNames = null,
        string? excludeFileName = null,
        CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        var backupsDir = await GetBackupsDirectoryAsync(ct);
        if (!Directory.Exists(backupsDir)) return 0;

        if (retentionDays is null)
        {
            var raw = await settings.GetAsync(SettingKeys.BackupsRetentionDays, ct);
            retentionDays = int.TryParse(raw, out var d) && d >= 0 ? d : DefaultRetentionDays;
        }

        if (maxCount is null)
        {
            var raw = await settings.GetAsync(SettingKeys.BackupsMaxCount, ct);
            maxCount = int.TryParse(raw, out var c) && c >= 0 ? c : DefaultMaxCount;
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeFileNames is not null)
        {
            foreach (var name in excludeFileNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    excluded.Add(Path.GetFileName(name));
            }
        }
        if (!string.IsNullOrWhiteSpace(excludeFileName))
        {
            excluded.Add(Path.GetFileName(excludeFileName));
        }

        var candidates = Directory.EnumerateFiles(backupsDir, "backup-*.zip", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var ts = ParseTimestamp(name) ?? File.GetCreationTimeUtc(path);
                return new { Path = path, FileName = name, Timestamp = ts };
            })
            .OrderByDescending(x => x.Timestamp)
            .ToList();

        // Safety invariant: Always keep at least the newest backup
        if (candidates.Count <= 1) return 0;

        var now = time.GetUtcNow().UtcDateTime;
        var ageCutoff = retentionDays.Value > 0 ? now.AddDays(-retentionDays.Value) : (DateTime?)null;

        var toDelete = new List<string>();
        for (var i = 1; i < candidates.Count; i++)
        {
            var item = candidates[i];
            if (excluded.Contains(item.FileName))
            {
                continue;
            }

            var pruneByAge = ageCutoff.HasValue && item.Timestamp < ageCutoff.Value;
            var pruneByCount = maxCount.Value > 0 && i >= maxCount.Value;

            if (pruneByAge || pruneByCount)
            {
                toDelete.Add(item.Path);
            }
        }

        var deletedCount = 0;
        foreach (var path in toDelete)
        {
            try
            {
                var name = Path.GetFileName(path);
                if (excluded.Contains(name))
                {
                    continue;
                }
                File.Delete(path);
                deletedCount++;
                await audit.WriteAsync("backup.pruned", "Backup", name, $"Pruned backup archive {name}", user ?? "system");
                logger.LogInformation("Pruned backup archive {FileName}", name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to prune backup archive at {Path}", path);
            }
        }

        return deletedCount;
    }

    /// <summary>
    /// Creates a portable listing export .zip organized by shop and listing:
    /// shops/{shop_id}_{shop_slug}/listings/{etsy_listing_id}_{listing_slug}/
    /// - shop.json
    /// - listing.json
    /// - description.txt (placeholders preserved)
    /// - rendered_description.txt
    /// - media/
    /// </summary>
    public async Task<string> CreateListingExportAsync(string? user = null, CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        var backupsDir = await GetBackupsDirectoryAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var baseFileName = $"etsy-listings-export-{now:yyyyMMdd-HHmmss}";
        var fileName = $"{baseFileName}.zip";
        var finalZipPath = Path.Combine(backupsDir, fileName);
        if (File.Exists(finalZipPath))
        {
            var counter = 1;
            while (File.Exists(Path.Combine(backupsDir, $"{baseFileName}-{counter}.zip")))
            {
                counter++;
            }
            fileName = $"{baseFileName}-{counter}.zip";
            finalZipPath = Path.Combine(backupsDir, fileName);
        }
        var tempZipPath = Path.Combine(backupsDir, $"{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var shops = await db.Shops.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
            var listings = await db.Listings.AsNoTracking()
                .Include(l => l.Images)
                .Include(l => l.Videos)
                .Include(l => l.Products)
                    .ThenInclude(p => p.Offerings)
                .OrderBy(l => l.Id)
                .ToListAsync(ct);

            var descriptions = await db.ListingDescriptions.AsNoTracking()
                .ToDictionaryAsync(d => d.ListingId, ct);

            var manifest = new
            {
                version = 1,
                app_version = AppVersion.Version,
                created_at_utc = now,
                backup_type = "listings_export",
                shop_count = shops.Count,
                listing_count = listings.Count
            };

            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. Root manifest.json
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var entryStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, ct);
                }

                // 2. Iterate shops
                foreach (var shop in shops)
                {
                    var shopSlug = Slugify(shop.Name);
                    var shopDir = $"shops/{shop.Id}_{shopSlug}";

                    // shop.json
                    var shopData = new
                    {
                        shop_id = shop.Id,
                        etsy_shop_id = shop.EtsyShopId,
                        name = shop.Name,
                        title = shop.Title,
                        url = shop.Url,
                        currency_code = shop.CurrencyCode,
                        is_active = shop.IsActive,
                        transaction_sold_count = shop.TransactionSoldCount,
                        connected_at_utc = shop.ConnectedAtUtc,
                        last_synced_at_utc = shop.LastSyncedAtUtc
                    };

                    var shopEntry = zip.CreateEntry($"{shopDir}/shop.json", CompressionLevel.Optimal);
                    await using (var entryStream = shopEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(entryStream, shopData, JsonOptions, ct);
                    }

                    // shop's listings
                    var shopListings = listings.Where(l => l.ShopId == shop.Id).ToList();
                    foreach (var listing in shopListings)
                    {
                        var listingSlug = Slugify(listing.Title);
                        var listingDir = $"{shopDir}/listings/{listing.EtsyListingId}_{listingSlug}";

                        // listing.json
                        var listingData = new
                        {
                            id = listing.Id,
                            etsy_listing_id = listing.EtsyListingId,
                            shop_id = listing.ShopId,
                            title = listing.Title,
                            state = listing.State,
                            price_amount = listing.PriceAmount,
                            price_divisor = listing.PriceDivisor,
                            currency_code = listing.CurrencyCode,
                            quantity = listing.Quantity,
                            skus = listing.Skus,
                            sku_pattern = listing.SkuPattern,
                            tags = listing.Tags,
                            materials = listing.Materials,
                            styles = listing.Styles,
                            taxonomy_id = listing.TaxonomyId,
                            shipping_profile_id = listing.ShippingProfileId,
                            return_policy_id = listing.ReturnPolicyId,
                            readiness_state_id = listing.ReadinessStateId,
                            shop_section_id = listing.ShopSectionId,
                            featured_rank = listing.FeaturedRank,
                            who_made = listing.WhoMade,
                            when_made = listing.WhenMade,
                            is_supply = listing.IsSupply,
                            listing_type = listing.ListingType,
                            is_customizable = listing.IsCustomizable,
                            is_personalizable = listing.IsPersonalizable,
                            is_taxable = listing.IsTaxable,
                            should_auto_renew = listing.ShouldAutoRenew,
                            has_variations = listing.HasVariations,
                            processing_min = listing.ProcessingMin,
                            processing_max = listing.ProcessingMax,
                            item_weight = listing.ItemWeight,
                            item_weight_unit = listing.ItemWeightUnit,
                            item_length = listing.ItemLength,
                            item_width = listing.ItemWidth,
                            item_height = listing.ItemHeight,
                            item_dimensions_unit = listing.ItemDimensionsUnit,
                            url = listing.Url,
                            views = listing.Views,
                            num_favorers = listing.NumFavorers,
                            personalization_json = listing.PersonalizationJson,
                            attributes = listing.Attributes,
                            inventory = listing.Products.Select(p => new
                            {
                                product_id = p.Id,
                                etsy_product_id = p.EtsyProductId,
                                sku = p.Sku,
                                is_deleted = p.IsDeleted,
                                property_values = p.PropertyValues,
                                offerings = p.Offerings.Select(o => new
                                {
                                    offering_id = o.Id,
                                    price = o.PriceAmount / (decimal)o.PriceDivisor,
                                    price_amount = o.PriceAmount,
                                    price_divisor = o.PriceDivisor,
                                    quantity = o.Quantity,
                                    is_enabled = o.IsEnabled
                                })
                            }),
                            images = listing.Images.Select(img => new
                            {
                                etsy_image_id = img.EtsyImageId,
                                rank = img.Rank,
                                alt_text = img.AltText,
                                file_name = img.FileName,
                                source_url = img.SourceUrl,
                                byte_size = img.ByteSize
                            }),
                            videos = listing.Videos.Select(v => new
                            {
                                etsy_video_id = v.EtsyVideoId,
                                width = v.Width,
                                height = v.Height,
                                video_state = v.VideoState,
                                file_name = v.FileName,
                                source_url = v.SourceUrl,
                                byte_size = v.ByteSize
                            }),
                            created_at_utc = listing.EtsyCreatedAtUtc,
                            updated_at_utc = listing.EtsyUpdatedAtUtc
                        };

                        var listingEntry = zip.CreateEntry($"{listingDir}/listing.json", CompressionLevel.Optimal);
                        await using (var entryStream = listingEntry.Open())
                        {
                            await JsonSerializer.SerializeAsync(entryStream, listingData, JsonOptions, ct);
                        }

                        // description.txt (raw with placeholders)
                        var rawDesc = descriptions.TryGetValue(listing.Id, out var desc)
                            ? desc.Source
                            : listing.Description;
                        var descEntry = zip.CreateEntry($"{listingDir}/description.txt", CompressionLevel.Optimal);
                        await using (var entryStream = descEntry.Open())
                        await using (var writer = new StreamWriter(entryStream))
                        {
                            await writer.WriteAsync(rawDesc);
                        }

                        // rendered_description.txt
                        var renderedEntry = zip.CreateEntry($"{listingDir}/rendered_description.txt", CompressionLevel.Optimal);
                        await using (var entryStream = renderedEntry.Open())
                        await using (var writer = new StreamWriter(entryStream))
                        {
                            await writer.WriteAsync(listing.Description);
                        }

                        // media/ images
                        foreach (var img in listing.Images)
                        {
                            if (string.IsNullOrEmpty(img.FileName)) continue;
                            var imgPath = Path.Combine(storage.ImagesDirectory, img.FileName.Length >= 2 ? img.FileName[..2] : "", img.FileName);
                            if (!File.Exists(imgPath))
                            {
                                // Fallback directly under ImagesDirectory
                                imgPath = Path.Combine(storage.ImagesDirectory, img.FileName);
                            }

                            if (File.Exists(imgPath))
                            {
                                var ext = Path.GetExtension(img.FileName);
                                var entryName = $"{listingDir}/media/photo_{img.Rank:D2}_{img.FileName}";
                                zip.CreateEntryFromFile(imgPath, entryName, CompressionLevel.Fastest);
                            }
                        }

                        // media/ videos
                        foreach (var vid in listing.Videos)
                        {
                            if (string.IsNullOrEmpty(vid.FileName)) continue;
                            var vidPath = Path.Combine(storage.VideosDirectory, vid.FileName.Length >= 2 ? vid.FileName[..2] : "", vid.FileName);
                            if (!File.Exists(vidPath))
                            {
                                vidPath = Path.Combine(storage.VideosDirectory, vid.FileName);
                            }

                            if (File.Exists(vidPath))
                            {
                                var entryName = $"{listingDir}/media/video_{vid.FileName}";
                                zip.CreateEntryFromFile(vidPath, entryName, CompressionLevel.Fastest);
                            }
                        }
                    }
                }
            }

            File.Move(tempZipPath, finalZipPath, overwrite: true);

            var sizeMb = new FileInfo(finalZipPath).Length / (1024.0 * 1024.0);
            await audit.WriteAsync("backup.export-created", "Backup", fileName,
                $"Created listings export {fileName} ({sizeMb:F2} MB)", user);
            logger.LogInformation("Created listings export {FileName} ({SizeMb:F2} MB)", fileName, sizeMb);

            return fileName;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Alias for <see cref="CreateListingExportAsync"/>.</summary>
    public Task<string> CreateExportAsync(CancellationToken ct = default) =>
        CreateListingExportAsync(user: "Export", ct: ct);

    /// <summary>
    /// Lists restorable backup archives and portable listing exports present in BackupsDirectory.
    /// Excludes deploy-* folders and temporary files.
    /// </summary>
    public IReadOnlyList<BackupFileInfo> ListBackups()
    {
        storage.EnsureDirectories();
        var backupsDir = GetBackupsDirectory();
        if (!Directory.Exists(backupsDir)) return [];

        var list = new List<BackupFileInfo>();
        foreach (var path in Directory.EnumerateFiles(backupsDir, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            string backupType;
            if (BackupFileNameRegex().IsMatch(name))
                backupType = "Restorable";
            else if (ExportFileNameRegex().IsMatch(name))
                backupType = "Export";
            else
                continue;

            var info = new FileInfo(path);
            var ts = ParseTimestamp(name) ?? info.CreationTimeUtc;
            var sizeMb = Math.Round(info.Length / (1024.0 * 1024.0), 2);

            list.Add(new BackupFileInfo(name, backupType, info.Length, sizeMb, ts));
        }

        return [.. list.OrderByDescending(b => b.CreatedAtUtc)];
    }

    /// <summary>
    /// Safely deletes a backup archive by file name.
    /// Ensures file stays within BackupsDirectory and is a restorable or export zip.
    /// </summary>
    public async Task<bool> DeleteBackupAsync(string fileName, string? user = null)
    {
        var path = ValidateAndResolvePath(fileName);
        if (!File.Exists(path)) return false;

        File.Delete(path);
        await audit.WriteAsync("backup.deleted", "Backup", fileName, $"Deleted backup {fileName}", user);
        logger.LogInformation("Deleted backup archive {FileName}", fileName);
        return true;
    }

    /// <summary>Synchronous delete method for convenience.</summary>
    public bool DeleteBackup(string fileName, string? user = null) =>
        DeleteBackupAsync(fileName, user).GetAwaiter().GetResult();

    /// <summary>
    /// Resolves and validates absolute path for a backup file, guarding against path traversal.
    /// </summary>
    public string GetBackupPath(string fileName)
    {
        var path = ValidateAndResolvePath(fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Backup file '{fileName}' was not found.", path);
        return path;
    }

    private string ValidateAndResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name cannot be empty.", nameof(fileName));

        if (fileName.IndexOfAny(['/', '\\']) >= 0 || fileName.Contains(".."))
            throw new ArgumentException($"Invalid file name '{fileName}'.", nameof(fileName));

        if (!BackupFileNameRegex().IsMatch(fileName) && !ExportFileNameRegex().IsMatch(fileName))
            throw new ArgumentException($"File name '{fileName}' is not an authorized backup archive.", nameof(fileName));

        var backupsDir = Path.GetFullPath(GetBackupsDirectory());
        var dirWithSep = backupsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(backupsDir, fileName));

        if (!fullPath.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path traversal attempt detected.", nameof(fileName));

        return fullPath;
    }

    private static DateTime? ParseTimestamp(string fileName)
    {
        var m = BackupFileNameRegex().Match(fileName);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        var mExp = ExportFileNameRegex().Match(fileName);
        if (mExp.Success && DateTime.TryParseExact(mExp.Groups[1].Value, "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dtExp))
        {
            return DateTime.SpecifyKind(dtExp, DateTimeKind.Utc);
        }

        return null;
    }

    private static string Slugify(string? text, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(text)) return "item";
        var slug = SlugRegex().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) return "item";
        return slug.Length > maxLength ? slug[..maxLength].TrimEnd('-') : slug;
    }

    /// <summary>
    /// Validates an on-disk backup archive (.zip) structure, security boundaries,
    /// manifest metadata, and database integrity. Accepts either a full path or a file name
    /// in the backups directory.
    /// </summary>
    public async Task<BackupValidationResult> ValidateBackupArchiveAsync(string zipPathOrFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(zipPathOrFileName))
            return new BackupValidationResult(false, "Backup archive path cannot be empty.");

        string resolvedPath;
        if (File.Exists(zipPathOrFileName))
        {
            resolvedPath = zipPathOrFileName;
        }
        else
        {
            try
            {
                resolvedPath = ValidateAndResolvePath(zipPathOrFileName);
            }
            catch
            {
                return new BackupValidationResult(false, $"Backup archive file was not found at '{zipPathOrFileName}'.");
            }
        }

        if (!File.Exists(resolvedPath))
            return new BackupValidationResult(false, $"Backup archive file was not found at '{resolvedPath}'.");

        try
        {
            await using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            // 1. Entry path security validation
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Contains("..") || name.StartsWith('/') || name.StartsWith('\\') || Path.IsPathRooted(name) ||
                    name.IndexOfAny([':', '\0']) >= 0)
                {
                    return new BackupValidationResult(false, $"Archive contains invalid or malicious entry path: '{name}'.");
                }
            }

            // 2. manifest.json verification
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
                return new BackupValidationResult(false, "Archive is missing manifest.json.");

            int version;
            string? backupType;
            long dbSizeBytes;
            int imageCount;
            int videoCount;
            int keyCount;

            await using (var manifestStream = manifestEntry.Open())
            {
                using var doc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
                var root = doc.RootElement;
                version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
                backupType = root.TryGetProperty("backup_type", out var bt) ? bt.GetString() : null;
                dbSizeBytes = root.TryGetProperty("database_size_bytes", out var dsb) ? dsb.GetInt64() : 0;
                imageCount = root.TryGetProperty("image_count", out var ic) ? ic.GetInt32() : 0;
                videoCount = root.TryGetProperty("video_count", out var vc) ? vc.GetInt32() : 0;
                keyCount = root.TryGetProperty("key_count", out var kc) ? kc.GetInt32() : 0;
            }

            if (!string.Equals(backupType, "full_restore", StringComparison.OrdinalIgnoreCase))
            {
                return new BackupValidationResult(false, $"Invalid backup_type '{backupType}'. Only 'full_restore' archives can be restored.");
            }

            // 3. Entry prefix validation for full_restore archive
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/') || name.EndsWith('\\')) continue;

                bool isManifest = string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase);
                bool isDb = string.Equals(name, "etsyshopmanager.db", StringComparison.OrdinalIgnoreCase);
                bool isImage = name.StartsWith("images/", StringComparison.OrdinalIgnoreCase);
                bool isVideo = name.StartsWith("videos/", StringComparison.OrdinalIgnoreCase);
                bool isKey = name.StartsWith("keys/", StringComparison.OrdinalIgnoreCase);

                if (!isManifest && !isDb && !isImage && !isVideo && !isKey)
                {
                    return new BackupValidationResult(false, $"Archive contains unauthorized entry: '{name}'.");
                }
            }

            // 3. etsyshopmanager.db verification
            var dbEntry = zip.GetEntry("etsyshopmanager.db");
            if (dbEntry is null)
                return new BackupValidationResult(false, "Archive is missing etsyshopmanager.db.");

            var tempDbPath = Path.Combine(Path.GetTempPath(), $"esm-validate-{Guid.NewGuid():N}.db");
            try
            {
                dbEntry.ExtractToFile(tempDbPath, overwrite: true);
                var fi = new FileInfo(tempDbPath);
                if (fi.Length == 0)
                    return new BackupValidationResult(false, "Database file in archive is empty.");

                await using var conn = new SqliteConnection($"Data Source={tempDbPath};Pooling=False");
                await conn.OpenAsync(ct);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var integrity = (string?)await cmd.ExecuteScalarAsync(ct);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    return new BackupValidationResult(false, $"SQLite integrity check failed: '{integrity}'.");

                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Shops', 'Listings', 'Settings');";
                var tableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                if (tableCount < 3)
                    return new BackupValidationResult(false, $"Database is missing essential application tables (found {tableCount} of 3).");

                return new BackupValidationResult(
                    IsValid: true,
                    ErrorMessage: null,
                    Version: version,
                    BackupType: backupType,
                    DbSizeBytes: fi.Length,
                    ImageCount: imageCount,
                    VideoCount: videoCount,
                    KeyCount: keyCount);
            }
            finally
            {
                if (File.Exists(tempDbPath))
                {
                    try { File.Delete(tempDbPath); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            return new BackupValidationResult(false, $"Corrupted or invalid backup archive: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates an in-memory or network stream by spooling to a temporary disk file.
    /// If the stream is already a local FileStream, validates directly without an extra copy.
    /// </summary>
    public async Task<BackupValidationResult> ValidateBackupStreamAsync(Stream zipStream, CancellationToken ct = default)
    {
        if (zipStream is FileStream fileStream && File.Exists(fileStream.Name))
        {
            try
            {
                return await ValidateBackupArchiveAsync(fileStream.Name, ct);
            }
            catch (IOException)
            {
                // Fall back to temp file spooling if the file cannot be shared or opened concurrently
            }
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"esm-stream-val-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await zipStream.CopyToAsync(fs, ct);
            }
            return await ValidateBackupArchiveAsync(tempPath, ct);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Restores a full backup from an existing file in the backups directory.
    /// Performs pre-validation, creates an automated safety backup of current live state,
    /// performs online SQLite restoration, extracts media and keys, clears settings cache,
    /// and writes an audit log entry.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreBackupAsync(string fileName, string? user = null, CancellationToken ct = default)
    {
        var fullZipPath = ValidateAndResolvePath(fileName);
        if (!File.Exists(fullZipPath))
            return new BackupRestoreResult(false, $"Backup file '{fileName}' was not found.");

        return await RestoreFromZipFileAsync(fullZipPath, fileName, user, ct);
    }

    /// <summary>
    /// Restores a full backup from an uploaded stream.
    /// If the stream is already a local FileStream, restores directly without an extra copy.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreBackupFromStreamAsync(
        Stream zipStream,
        string sourceLabel,
        string? user = null,
        CancellationToken ct = default)
    {
        if (zipStream is FileStream fileStream && File.Exists(fileStream.Name))
        {
            try
            {
                return await RestoreFromZipFileAsync(fileStream.Name, sourceLabel, user, ct);
            }
            catch (IOException)
            {
                // Fall back to temp file spooling if the file cannot be shared or opened concurrently
            }
        }

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"esm-stream-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await zipStream.CopyToAsync(fs, ct);
            }
            return await RestoreFromZipFileAsync(tempZipPath, sourceLabel, user, ct);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore */ }
            }
        }
    }

    private async Task<BackupRestoreResult> RestoreFromZipFileAsync(
        string zipFilePath,
        string sourceLabel,
        string? user,
        CancellationToken ct)
    {
        // Step 1: Pre-validation
        var validation = await ValidateBackupArchiveAsync(zipFilePath, ct);
        if (!validation.IsValid)
            return new BackupRestoreResult(false, validation.ErrorMessage);

        // Step 2: Automated Pre-Restore Safety Backup
        string safetyBackupName;
        try
        {
            var targetFileName = Path.GetFileName(zipFilePath);
            safetyBackupName = await CreateRestorableBackupAsync(
                user: $"pre-restore:{user ?? "admin"}",
                prune: false,
                excludeFileName: targetFileName,
                ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create pre-restore safety backup before restoring {SourceLabel}", sourceLabel);
            return new BackupRestoreResult(false, $"Pre-restore safety backup failed: {ex.Message}");
        }

        // Step 3: SQLite Online Restore
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"esm-restore-db-{Guid.NewGuid():N}.db");
        try
        {
            using (var zip = ZipFile.OpenRead(zipFilePath))
            {
                var dbEntry = zip.GetEntry("etsyshopmanager.db")!;
                dbEntry.ExtractToFile(tempDbPath, overwrite: true);
            }

            var srcConnStr = $"Data Source={tempDbPath};Pooling=False";
            await using (var source = new SqliteConnection(srcConnStr))
            {
                await source.OpenAsync(ct);
                await using (var dest = new SqliteConnection(storage.ConnectionString))
                {
                    await dest.OpenAsync(ct);
                    await Task.Run(() => source.BackupDatabase(dest), ct);

                    await using var cmd = dest.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore database from {SourceLabel}", sourceLabel);
            return new BackupRestoreResult(false, $"Database restore failed: {ex.Message}", PreRestoreSafetyBackup: safetyBackupName);
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                try { File.Delete(tempDbPath); } catch { /* ignore */ }
            }
        }

        // Step 4: Media and Keys Extraction
        int restoredImages = 0;
        int restoredVideos = 0;
        int restoredKeys = 0;

        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            var resolvedRootWithSep = storage.ResolvedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                      + Path.DirectorySeparatorChar;

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.FullName, "etsyshopmanager.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isImage = entry.FullName.StartsWith("images/", StringComparison.OrdinalIgnoreCase);
                bool isVideo = entry.FullName.StartsWith("videos/", StringComparison.OrdinalIgnoreCase);
                bool isKey = entry.FullName.StartsWith("keys/", StringComparison.OrdinalIgnoreCase);

                if (!isImage && !isVideo && !isKey) continue;

                var targetPath = Path.GetFullPath(Path.Combine(storage.ResolvedRoot, entry.FullName));
                if (!targetPath.StartsWith(resolvedRootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Path traversal attempt in archive entry: {entry.FullName}");
                }

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                entry.ExtractToFile(targetPath, overwrite: true);

                if (isImage) restoredImages++;
                else if (isVideo) restoredVideos++;
                else if (isKey) restoredKeys++;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract media or keys from {SourceLabel}", sourceLabel);
            return new BackupRestoreResult(false, $"Media/keys extraction failed: {ex.Message}", PreRestoreSafetyBackup: safetyBackupName);
        }

        // Step 5: Invalidate in-memory settings cache
        settings.ClearCache();

        // Step 6: Post-restore audit logging
        var details = $"Restored backup from {sourceLabel}. Pre-restore safety backup: {safetyBackupName}. Restored {restoredImages} images, {restoredVideos} videos, {restoredKeys} keys.";
        await audit.WriteAsync("backup.restored", "Backup", sourceLabel, details, user, ct);
        logger.LogInformation("Restored backup from {SourceLabel}. Safety backup: {SafetyBackup}", sourceLabel, safetyBackupName);

        // Step 7: Return successful result
        return new BackupRestoreResult(
            Success: true,
            ErrorMessage: null,
            PreRestoreSafetyBackup: safetyBackupName,
            RestoredImages: restoredImages,
            RestoredVideos: restoredVideos,
            RestoredKeys: restoredKeys);
    }
}
