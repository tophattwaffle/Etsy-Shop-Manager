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

    [GeneratedRegex(@"^backup-(\d{8}-\d{6})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupFileNameRegex();

    [GeneratedRegex(@"^etsy-listings-export-(\d{8}-\d{6})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportFileNameRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlugRegex();

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
        CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        var now = time.GetUtcNow().UtcDateTime;
        var fileName = $"backup-{now:yyyyMMdd-HHmmss}.zip";
        var finalZipPath = Path.Combine(storage.BackupsDirectory, fileName);
        var tempZipPath = Path.Combine(storage.BackupsDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp");
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
            await PruneBackupsAsync(user: user ?? "system", ct: ct);

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
        CancellationToken ct = default)
    {
        storage.EnsureDirectories();
        if (!Directory.Exists(storage.BackupsDirectory)) return 0;

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

        var candidates = Directory.EnumerateFiles(storage.BackupsDirectory, "backup-*.zip", SearchOption.TopDirectoryOnly)
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
        var now = time.GetUtcNow().UtcDateTime;
        var fileName = $"etsy-listings-export-{now:yyyyMMdd-HHmmss}.zip";
        var finalZipPath = Path.Combine(storage.BackupsDirectory, fileName);
        var tempZipPath = Path.Combine(storage.BackupsDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp");

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
        if (!Directory.Exists(storage.BackupsDirectory)) return [];

        var list = new List<BackupFileInfo>();
        foreach (var path in Directory.EnumerateFiles(storage.BackupsDirectory, "*.zip", SearchOption.TopDirectoryOnly))
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

        var fullPath = Path.GetFullPath(Path.Combine(storage.BackupsDirectory, fileName));
        var backupsDir = Path.GetFullPath(storage.BackupsDirectory);

        if (!fullPath.StartsWith(backupsDir, StringComparison.OrdinalIgnoreCase))
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
}
