using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Infrastructure.Backups;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Shops;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Tier 5 Adversarial Coverage Hardening test suite for Requirements R1 through R4.
/// Verifies boundary conditions, concurrency, malformed payloads, unusual character sets,
/// state invalidation, and data integrity invariants without touching live Etsy endpoints.
/// </summary>
public class AdversarialTier5_R1_R4_Tests
{
    private const long ColorPropertyId = VariationEditor.CustomProperty1;
    private const long SizePropertyId = 100L;

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

    private sealed class ThreadSafeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string?> _settings = new();
        private readonly object _lock = new();

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_settings.TryGetValue(key, out var v) ? v : null);
            }
        }

        public Task SetAsync(string key, string? value, bool isEncrypted = false, string? updatedBy = null, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _settings[key] = value;
                return Task.CompletedTask;
            }
        }

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_settings.ContainsKey(key) && !string.IsNullOrEmpty(_settings[key]));
            }
        }
    }

    private sealed class FileBasedDbFactory(string connectionString) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    // =============================================================================================
    // R1: SHOP DEFAULTS, NEW LISTING CREATION & CLONER DEFAULTS
    // =============================================================================================

    [Fact]
    public async Task R1_ShopDefaults_ConcurrentSavesAndGets_ThreadSafetyAndIntegrity()
    {
        var settings = new ThreadSafeSettingsStore();
        var audit = new RecordingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        // 20 concurrent threads writing and reading defaults across multiple shops
        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            var shopId = (i % 3) + 1;
            var defaults = new ShopDefaults(
                ShippingProfileId: 1000 + i,
                ReadinessStateId: 2000 + i,
                ReturnPolicyId: 3000 + i,
                ShopSectionId: 4000 + i,
                WhoMade: "i_did",
                WhenMade: "made_to_order",
                TaxonomyId: 5000 + i);

            await service.SaveAsync(shopId, defaults, $"user{i}");
            var loaded = await service.GetAsync(shopId);
            Assert.NotNull(loaded);
            Assert.True(loaded.ShippingProfileId >= 1000);
        });

        await Task.WhenAll(tasks);

        // All 3 shops have valid persisted defaults
        for (var s = 1; s <= 3; s++)
        {
            var finalDefaults = await service.GetAsync(s);
            Assert.NotNull(finalDefaults);
            Assert.NotNull(finalDefaults.ShippingProfileId);
        }
    }

    [Theory]
    [InlineData("{ malformed json string")]
    [InlineData("[]")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task R1_ShopDefaults_CorruptedAndMalformedJson_GracefulDegradation(string badJson)
    {
        var settings = new ThreadSafeSettingsStore();
        var audit = new RecordingAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        await settings.SetAsync(ShopDefaultsService.SettingKey(1), badJson);
        var result = await service.GetAsync(1);
        Assert.Null(result);
    }

    [Fact]
    public async Task R1_ListingCreation_UnicodeInternationalCharacterSets_ValidationAndFormSerialization()
    {
        using var shop = new FakeEtsyShop();
        var audit = new RecordingAuditLog();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), audit,
            NullLogger<ListingCreationService>.Instance);

        // Populate lookup tables
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 501, Title = "Standard" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 601, ReadinessState = "ready_to_ship", DisplayLabel = "1-3 days" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = FakeEtsyShop.ShopId, EtsyReturnPolicyId = 701, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = FakeEtsyShop.ShopId, EtsySectionId = 801, Title = "Art" });
            await db.SaveChangesAsync();
        }

        // Japanese Kanji/Hiragana, Cyrillic, and Copyright symbol in title (valid per ListingTextRules)
        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "ハンドメイド Ceramic Mug (陶器) - Красивая кружка ©",
            Price = 24.99m,
            Quantity = 3,
            TaxonomyId = 1234,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
            ReturnPolicyId = 701,
            ShopSectionId = 801,
            Description = "Wheel-thrown speckled mug with traditional glaze.\n\nCrafted with care in Tokyo.",
            Tags = ["japan mug", "керамика", "chawan"],
            Materials = ["clay", "глина"],
        };

        var problems = service.Validate(model);
        Assert.Empty(problems);

        var localId = await service.CreateDraftAsync(model, "adversarial_tester");
        Assert.True(localId > 0);

        // Verify sent form payload
        Assert.Single(shop.Writes);
        var write = shop.Writes[0];
        Assert.Equal("POST", write.Method);
        var form = HttpUtility.ParseQueryString(write.Body);
        Assert.Equal("ハンドメイド Ceramic Mug (陶器) - Красивая кружка ©", form["title"]);
        Assert.Equal("japan mug,керамика,chawan", form["tags"]);
        Assert.Equal("clay,глина", form["materials"]);
    }

    [Theory]
    [InlineData(0, 1, false, "Price must be greater than 0.")]
    [InlineData(-0.01, 1, false, "Price must be greater than 0.")]
    [InlineData(19.99, 0, false, "Quantity must be at least 1.")]
    [InlineData(19.99, -1, false, "Quantity must be at least 1.")]
    [InlineData(0.20, 1, true, null)]
    [InlineData(99999.99, 9999, true, null)]
    public void R1_ListingCreation_PriceAndQuantityBoundaries_Validation(decimal price, int quantity, bool expectedValid, string? expectedError)
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new RecordingAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Valid Listing Title",
            Price = price,
            Quantity = quantity,
            TaxonomyId = 100,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
        };

        var problems = service.Validate(model);
        if (expectedValid)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problems, p => p.Contains(expectedError!));
        }
    }

    [Fact]
    public void R1_ListingCreation_LookupsValidation_ForeignAndNonExistentProfiles()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new RecordingAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        var lookups = new EditorLookups(
            ShippingProfiles: new Dictionary<long, string> { [501] = "Standard Shipping" },
            ProcessingProfiles: new Dictionary<long, string> { [601] = "1-2 days" },
            ReturnPolicies: new Dictionary<long, string> { [701] = "30-day returns" },
            Sections: new Dictionary<long, string> { [801] = "Featured" },
            ProductionPartners: new Dictionary<long, string>(),
            Categories: new Dictionary<long, string>(),
            CurrencyCode: "USD");

        // Invalid profile IDs not in lookups
        var invalidModel = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Valid Listing Title",
            Price = 15.00m,
            Quantity = 2,
            TaxonomyId = 100,
            ShippingProfileId = 999, // Alien shipping profile
            ReadinessStateId = 999,  // Alien processing profile
            ReturnPolicyId = 999,    // Alien return policy
            ShopSectionId = 999,     // Alien shop section
        };

        var problems = service.Validate(invalidModel, lookups);
        Assert.Contains(problems, p => p.Contains("Choose a shipping profile belonging to this shop."));
        Assert.Contains(problems, p => p.Contains("Choose a processing profile belonging to this shop."));
        Assert.Contains(problems, p => p.Contains("The return policy doesn't belong to this shop."));
        Assert.Contains(problems, p => p.Contains("The shop section doesn't belong to this shop."));

        // Valid model matching lookups
        var validModel = invalidModel with
        {
            ShippingProfileId = 501,
            ReadinessStateId = 601,
            ReturnPolicyId = 701,
            ShopSectionId = 801,
        };
        Assert.Empty(service.Validate(validModel, lookups));
    }

    [Fact]
    public async Task R1_ListingCreation_InactiveShopOrReauthRequired_BlocksCreation()
    {
        using var shop = new FakeEtsyShop();
        var audit = new RecordingAuditLog();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), audit,
            NullLogger<ListingCreationService>.Instance);

        // Deactivate shop
        await using (var db = shop.Db.CreateDbContext())
        {
            var s = await db.Shops.Include(x => x.Token).FirstAsync(x => x.Id == FakeEtsyShop.ShopId);
            s.IsActive = false;
            await db.SaveChangesAsync();
        }

        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Valid Listing Title",
            Price = 10.00m,
            Quantity = 1,
            TaxonomyId = 100,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDraftAsync(model));
        Assert.Contains("must be reconnected to Etsy first", ex.Message);
        Assert.Empty(shop.Writes); // Zero writes to Etsy
    }

    [Fact]
    public async Task R1_ListingCloner_DefaultsFallbackHierarchy_TargetShopDefaults()
    {
        using var shop = new FakeEtsyShop();
        var settings = new ThreadSafeSettingsStore();
        var audit = new RecordingAuditLog();
        var defaultsService = new ShopDefaultsService(settings, audit);

        const int targetShopId = 2;

        // Target shop defaults has ShippingProfile and ReturnPolicy, but null ReadinessState
        await defaultsService.SaveAsync(targetShopId, new ShopDefaults(
            ShippingProfileId: 555,
            ReadinessStateId: null,
            ReturnPolicyId: 777,
            ShopSectionId: null), "admin");

        // Clone.LastChoice has fallback values for ReadinessState and ShopSection
        var lastChoice = new CloneSettings(
            TargetShopId: targetShopId,
            Title: "Last Clone Title",
            ShippingProfileId: 444,
            ReadinessStateId: 666,
            ReturnPolicyId: 888,
            ShopSectionId: 999);
        await settings.SetAsync(ListingCloner.LastChoiceKey(targetShopId), JsonSerializer.Serialize(lastChoice));

        // Add a source listing in shop 1
        shop.Listings.Add(FakeEtsyShop.Listing(100, "Source Listing"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int sourceListingDbId;
        await using (var db = shop.Db.CreateDbContext())
        {
            sourceListingDbId = await db.Listings.Where(l => l.EtsyListingId == 100).Select(l => l.Id).SingleAsync();
        }

        var cloner = new ListingCloner(
            shop.Db,
            shop.Provider.GetRequiredService<ImageStore>(),
            shop.Provider.GetRequiredService<IJobQueue>(),
            settings,
            audit,
            TimeProvider.System,
            defaultsService);

        var choices = await cloner.DefaultsAsync(sourceListingDbId, targetShopId);

        // Pre-fill hierarchy: defaults take priority, fallback to last choice
        Assert.Equal(555, choices.ShippingProfileId); // From defaults
        Assert.Equal(666, choices.ReadinessStateId);  // From last choice fallback
        Assert.Equal(777, choices.ReturnPolicyId);    // From defaults
        Assert.Equal(999, choices.ShopSectionId);     // From last choice fallback
    }

    // =============================================================================================
    // R2: BACKUPS, RETENTION PRUNING & PORTABLE EXPORTS
    // =============================================================================================

    [Fact]
    public async Task R2_BackupService_RapidSuccessiveBackups_NoLockLeakOrCorruption()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new ThreadSafeSettingsStore();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        // Seed file-based SQLite database with schema
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Shops.Add(new Shop { EtsyShopId = 1, EtsyUserId = 1, Name = "Shop A", CurrencyCode = "USD" });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(temp.Options, factory, settings, audit, TimeProvider.System, NullLogger<BackupService>.Instance);

        // Execute 2 backups in rapid succession across second boundary
        var b1 = await service.CreateRestorableBackupAsync(user: "user1");
        await Task.Delay(1100);
        var b2 = await service.CreateRestorableBackupAsync(user: "user2");

        Assert.NotEqual(b1, b2);

        // Verify both zip files exist, are valid archives, and contain manifest and db
        foreach (var backupName in new[] { b1, b2 })
        {
            var path = Path.Combine(temp.Options.BackupsDirectory, backupName);
            Assert.True(File.Exists(path));
            using var zip = ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("etsyshopmanager.db"));
        }
    }

    [Fact]
    public async Task R2_BackupService_RetentionPruning_ZeroRetentionAndZeroMaxCount_NeverWipesBackups()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new ThreadSafeSettingsStore();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        var service = new BackupService(temp.Options, factory, settings, audit, TimeProvider.System, NullLogger<BackupService>.Instance);

        // Create 4 dummy backups
        for (var i = 1; i <= 4; i++)
        {
            var zipPath = Path.Combine(temp.Options.BackupsDirectory, $"backup-2026100{i}-120000.zip");
            await File.WriteAllBytesAsync(zipPath, [1, 2, 3]);
        }

        // When retentionDays = 0 and maxCount = 0 (pruning disabled), nothing should be deleted
        var pruned = await service.PruneBackupsAsync(retentionDays: 0, maxCount: 0);
        Assert.Equal(0, pruned);

        var remaining = Directory.GetFiles(temp.Options.BackupsDirectory, "backup-*.zip");
        Assert.Equal(4, remaining.Length);
    }

    [Fact]
    public async Task R2_BackupService_RetentionPruning_AlwaysProtectsNewestBackup()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new ThreadSafeSettingsStore();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        var service = new BackupService(temp.Options, factory, settings, audit, TimeProvider.System, NullLogger<BackupService>.Instance);

        // Create 3 old backups (all from 2020)
        for (var i = 1; i <= 3; i++)
        {
            var zipPath = Path.Combine(temp.Options.BackupsDirectory, $"backup-2020010{i}-120000.zip");
            await File.WriteAllBytesAsync(zipPath, [1, 2, 3]);
        }

        // Prune with aggressive policy: retentionDays = 1, maxCount = 1
        var pruned = await service.PruneBackupsAsync(retentionDays: 1, maxCount: 1);
        Assert.Equal(2, pruned);

        // Safety invariant: The single newest backup (backup-20200103-120000.zip) MUST remain
        var remaining = Directory.GetFiles(temp.Options.BackupsDirectory, "backup-*.zip");
        Assert.Single(remaining);
        Assert.EndsWith("backup-20200103-120000.zip", remaining[0]);
    }

    [Fact]
    public async Task R2_BackupService_RetentionPruning_NeverTouchesForeignOrDeployZips()
    {
        using var temp = new TempStorage();
        var audit = new RecordingAuditLog();
        var settings = new ThreadSafeSettingsStore();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        var service = new BackupService(temp.Options, factory, settings, audit, TimeProvider.System, NullLogger<BackupService>.Instance);

        // Place foreign / deploy zips in the backups folder
        var foreignFiles = new[]
        {
            "deploy-slot1.zip",
            "deploy-backup-20261021.zip",
            "etsy-listings-export-20261021-120000.zip",
            "manual-restore.zip",
            "notes.txt",
        };

        foreach (var f in foreignFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(temp.Options.BackupsDirectory, f), "precious data");
        }

        // Add 2 backup-*.zip files
        await File.WriteAllBytesAsync(Path.Combine(temp.Options.BackupsDirectory, "backup-20200101-100000.zip"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(temp.Options.BackupsDirectory, "backup-20200102-100000.zip"), [2]);

        // Prune down to 1 backup
        await service.PruneBackupsAsync(retentionDays: 1, maxCount: 1);

        // Verify foreign files remain completely untouched
        foreach (var f in foreignFiles)
        {
            Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, f)), $"Expected {f} to be protected.");
        }
    }

    [Theory]
    [InlineData("../backup-20261021-120000.zip")]
    [InlineData("..\\backup-20261021-120000.zip")]
    [InlineData("sub/backup-20261021-120000.zip")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("unauthorized.zip")]
    public async Task R2_BackupService_PathTraversalAndMaliciousFilenames_Rejected(string maliciousFileName)
    {
        using var temp = new TempStorage();
        var service = new BackupService(temp.Options, new FileBasedDbFactory(temp.Options.ConnectionString),
            new ThreadSafeSettingsStore(), new RecordingAuditLog(), TimeProvider.System, NullLogger<BackupService>.Instance);

        Assert.Throws<ArgumentException>(() => service.GetBackupPath(maliciousFileName));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteBackupAsync(maliciousFileName));
    }

    [Fact]
    public async Task R2_BackupService_PortableExport_UnicodeAndSpecialCharacters_ValidZipIntegrity()
    {
        using var temp = new TempStorage();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var shop = new Shop
            {
                EtsyShopId = 999,
                EtsyUserId = 888,
                Name = "花と器 Studio",
                Url = "https://www.etsy.com/shop/HanaToUtsuwa",
                CurrencyCode = "JPY",
                IsActive = true,
            };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 12345678,
                Title = "Handmade 陶器 Bowl ✨ Special",
                State = "active",
                PriceAmount = 4500,
                PriceDivisor = 100,
                CurrencyCode = "JPY",
                Quantity = 5,
                Description = "A lovely bowl made by {{shop.name}}.",
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();

            db.ListingDescriptions.Add(new ListingDescription
            {
                ListingId = listing.Id,
                Source = "A lovely bowl made by {{shop.name}}.",
                Rendered = "A lovely bowl made by 花と器 Studio.",
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = new BackupService(temp.Options, factory, new ThreadSafeSettingsStore(),
            new RecordingAuditLog(), TimeProvider.System, NullLogger<BackupService>.Instance);

        var exportFile = await service.CreateListingExportAsync("tester");
        Assert.NotNull(exportFile);

        var exportPath = Path.Combine(temp.Options.BackupsDirectory, exportFile);
        Assert.True(File.Exists(exportPath));

        // Open zip and inspect structure
        using var zip = ZipFile.OpenRead(exportPath);
        Assert.NotNull(zip.GetEntry("manifest.json"));

        // Verify shop and listing entries
        var listingJsonEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("listing.json"));
        Assert.NotNull(listingJsonEntry);

        var descEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("description.txt"));
        Assert.NotNull(descEntry);
        using var r = new StreamReader(descEntry.Open());
        var rawDesc = await r.ReadToEndAsync();
        Assert.Equal("A lovely bowl made by {{shop.name}}.", rawDesc);
    }

    [Fact]
    public async Task R2_BackupService_PortableExport_MissingMediaOnDisk_GracefulHandling()
    {
        using var temp = new TempStorage();
        var factory = new FileBasedDbFactory(temp.Options.ConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var shop = new Shop { EtsyShopId = 1, EtsyUserId = 1, Name = "ShopMedia", CurrencyCode = "USD" };
            db.Shops.Add(shop);
            await db.SaveChangesAsync();

            var listing = new Listing
            {
                ShopId = shop.Id,
                EtsyListingId = 9999,
                Title = "Media Listing",
                Description = "Description",
                Images = [new ListingImage { EtsyImageId = 101, Rank = 1, FileName = "ghost_image.jpg" }],
                Videos = [new ListingVideo { EtsyVideoId = 201, SourceUrl = "https://etsy.com/video", FileName = "ghost_video.mp4" }],
            };
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
        }

        var service = new BackupService(temp.Options, factory, new ThreadSafeSettingsStore(),
            new RecordingAuditLog(), TimeProvider.System, NullLogger<BackupService>.Instance);

        // Missing physical image and video files should not throw FileNotFoundException
        var exportFile = await service.CreateListingExportAsync();
        Assert.NotNull(exportFile);
        Assert.True(File.Exists(Path.Combine(temp.Options.BackupsDirectory, exportFile)));
    }

    // =============================================================================================
    // R3: PLACEHOLDER ENGINE & TEXT BLOCKS
    // =============================================================================================

    [Fact]
    public void R3_PlaceholderEngine_CaseInsensitiveAndWhitespaceVariations()
    {
        var shop = new Shop { Name = "Acme Pottery", Url = "https://www.etsy.com/shop/AcmePottery" };
        var local = new JsonObject
        {
            ["title"] = "Artisan Clay Vase",
            ["materials"] = new JsonArray("clay", "glaze"),
            ["tags"] = new JsonArray("pottery", "decor"),
            ["item_length"] = 10.5m,
            ["item_width"] = 5.0m,
            ["item_height"] = 5.0m,
            ["item_dimensions_unit"] = "in",
        };
        var context = new PlaceholderContext(shop, local, ["SKU-ART-01"], new Dictionary<string, string>());

        var source = "Shop: {{   sHoP.nAmE   }} | URL: {{  SHOP.URL  }}\n" +
                     "Title: {{  LISTING.TITLE  }} | SKU: {{   Listing.Sku   }}\n" +
                     "Materials: {{  listing.materials  }} | Dims: {{  Listing.Dimensions  }}";

        var (rendered, unresolved) = PlaceholderEngine.Render(source, context);

        Assert.Empty(unresolved);
        Assert.Contains("Shop: Acme Pottery | URL: https://www.etsy.com/shop/AcmePottery", rendered);
        Assert.Contains("Title: Artisan Clay Vase | SKU: SKU-ART-01", rendered);
        Assert.Contains("Materials: clay, glaze | Dims: 10.5 × 5 × 5 in", rendered);
    }

    [Fact]
    public void R3_PlaceholderEngine_ComplexDimensionAndUnitFormatting()
    {
        var shop = new Shop { Name = "Shop" };

        // 1. All dimensions present with unit
        var localWithUnit = new JsonObject
        {
            ["item_length"] = 12.5m,
            ["item_width"] = 8.0m,
            ["item_height"] = 3.25m,
            ["item_dimensions_unit"] = "cm",
            ["item_weight"] = 1.75m,
            ["item_weight_unit"] = "kg",
        };
        var context1 = new PlaceholderContext(shop, localWithUnit, [], new Dictionary<string, string>());
        var (res1, _) = PlaceholderEngine.Render("Dims: {{listing.dimensions}} | W: {{listing.weight}} {{listing.weight_unit}}", context1);
        Assert.Equal("Dims: 12.5 × 8 × 3.25 cm | W: 1.75 kg", res1);

        // 2. Dimensions present without unit
        var localNoUnit = new JsonObject
        {
            ["item_length"] = 10m,
            ["item_width"] = 5m,
            ["item_height"] = 2m,
        };
        var context2 = new PlaceholderContext(shop, localNoUnit, [], new Dictionary<string, string>());
        var (res2, _) = PlaceholderEngine.Render("Dims: {{listing.dimensions}}", context2);
        Assert.Equal("Dims: 10 × 5 × 2", res2);

        // 3. Partial dimensions (height is null) -> dimensions returns empty string
        var localPartial = new JsonObject
        {
            ["item_length"] = 10m,
            ["item_width"] = 5m,
        };
        var context3 = new PlaceholderContext(shop, localPartial, [], new Dictionary<string, string>());
        var (res3, _) = PlaceholderEngine.Render("Dims: [{{listing.dimensions}}]", context3);
        Assert.Equal("Dims: []", res3);
    }

    [Fact]
    public void R3_PlaceholderEngine_MaxRecursionDepth_TerminatesGracefully()
    {
        var shop = new Shop { Name = "Shop" };

        // Chain of 8 text blocks: b1 -> b2 -> b3 -> b4 -> b5 -> b6 -> b7 -> b8
        var blocks = new Dictionary<string, string>
        {
            ["b1"] = "-> {{text.b2}}",
            ["b2"] = "-> {{text.b3}}",
            ["b3"] = "-> {{text.b4}}",
            ["b4"] = "-> {{text.b5}}",
            ["b5"] = "-> {{text.b6}}",
            ["b6"] = "-> {{text.b7}}",
            ["b7"] = "-> {{text.b8}}",
            ["b8"] = "FINAL",
        };
        var context = new PlaceholderContext(shop, new JsonObject(), [], blocks);

        // Multi-pass expansion capped at MaxRecursionDepth = 5 passes
        var (rendered, unresolved) = PlaceholderEngine.Render("Start: {{text.b1}}", context);
        Assert.NotNull(rendered);
        // It expands 5 times: b1 -> b2 -> b3 -> b4 -> b5 -> b6. b6 remains unexpanded.
        Assert.Contains("{{text.b6}}", rendered);
        Assert.Contains("b6", unresolved);
    }

    [Fact]
    public void R3_PlaceholderEngine_DiamondAndComplexCycles_DetectedByHasCircularReference()
    {
        // Complex diamond cycle:
        // A -> B and C
        // B -> D
        // C -> D
        // D -> A (cycle back to A)
        var blocksWithCycle = new Dictionary<string, string>
        {
            ["b"] = "calls {{text.d}}",
            ["c"] = "calls {{text.d}}",
            ["d"] = "calls {{text.a}}",
        };
        var textA = "calls {{text.b}} and {{text.c}}";
        Assert.True(PlaceholderEngine.HasCircularReference("a", textA, blocksWithCycle));

        // Diamond DAG without back-cycle:
        // A -> B and C
        // B -> D
        // C -> D
        // D -> E (no cycle)
        var blocksWithoutCycle = new Dictionary<string, string>
        {
            ["b"] = "calls {{text.d}}",
            ["c"] = "calls {{text.d}}",
            ["d"] = "calls End",
        };
        Assert.False(PlaceholderEngine.HasCircularReference("a", textA, blocksWithoutCycle));
    }

    [Fact]
    public void R3_PlaceholderEngine_NestedPlaceholdersInsideTemplates_BothDirectAndNestedResolve()
    {
        var shop = new Shop { Name = "Starlight Crafts", Url = "https://www.etsy.com/shop/Starlight" };
        var local = new JsonObject
        {
            ["title"] = "Constellation Pendant",
            ["materials"] = new JsonArray("silver", "sapphire"),
        };

        var blocks = new Dictionary<string, string>
        {
            ["footer"] = "Crafted by {{shop.name}} using {{listing.materials}}.\nOrder from {{shop.url}}",
        };
        var context = new PlaceholderContext(shop, local, ["SKU-STAR-99"], blocks);

        var (rendered, unresolved) = PlaceholderEngine.Render("Description:\n{{text.footer}}", context);
        Assert.Empty(unresolved);
        Assert.Equal("Description:\nCrafted by Starlight Crafts using silver, sapphire.\nOrder from https://www.etsy.com/shop/Starlight", rendered);
    }

    [Fact]
    public async Task R3_TextBlockService_DeleteBlock_PreservesTextInListingDescriptions()
    {
        using var shop = new FakeEtsyShop();
        var blocks = shop.Provider.GetRequiredService<TextBlockService>();

        // Create a text block
        var (blockId, err) = await blocks.SaveAsync(null, "Organic Cotton Care", "organic-cotton",
            "Wash cold. Line dry only.", "admin");
        Assert.Null(err);
        Assert.NotNull(blockId);

        // Add a listing with description containing the block placeholder
        shop.Listings.Add(FakeEtsyShop.Listing(10, "Cotton T-Shirt"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        int listingDbId;
        await using (var db = shop.Db.CreateDbContext())
        {
            listingDbId = await db.Listings.Where(l => l.EtsyListingId == 10).Select(l => l.Id).SingleAsync();
        }

        await blocks.SaveDescriptionAsync(listingDbId, "T-Shirt Overview\n\n{{text.organic-cotton}}", "admin");

        // Verify description is saved with placeholder
        var savedSource = await blocks.SourceAsync(listingDbId);
        Assert.Contains("{{text.organic-cotton}}", savedSource);

        // Delete text block
        await blocks.DeleteAsync(blockId.Value, "admin");

        // Listing description source should now contain the expanded text word-for-word
        var sourceAfterDelete = await blocks.SourceAsync(listingDbId);
        Assert.DoesNotContain("{{text.organic-cotton}}", sourceAfterDelete);
        Assert.Contains("Wash cold. Line dry only.", sourceAfterDelete);
    }

    // =============================================================================================
    // R4: SKU PATTERNS, VARIATION VALUE REORDERING & INVENTORY WRITER
    // =============================================================================================

    [Theory]
    [InlineData(""""///1/2\ - XL \ Glitter ★ & Special///"""", "1-2-XL-Glitter-Special")]
    [InlineData("!@#$%^&*()_+={}|[]\\:\";'<>?,./", "_")]
    [InlineData("!@#$%^&*()+={}|[]\\:\";'<>?,./", "")]
    [InlineData("   ---Spacing---   ", "Spacing")]
    [InlineData("Red / White / Blue", "Red-White-Blue")]
    [InlineData("Размер-XL", "Размер-XL")]
    [InlineData("Größe-M", "Größe-M")]
    [InlineData("  ", "")]
    public void R4_SkuPatternResolver_SanitizeForSku_ExtremeAdversarialInputs(string input, string expected)
    {
        var sanitized = SkuPatternResolver.SanitizeForSku(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void R4_SkuPatternResolver_PropertyMatchingHierarchy_ExactVsNormalizedVsSuffix()
    {
        var variationValues = new Dictionary<string, string>
        {
            ["Primary Color"] = "Cobalt Blue",
            ["Shirt Size"] = "X-Large",
        };

        // 1. Exact match (case-insensitive)
        var sku1 = SkuPatternResolver.GenerateSku("{Primary Color}", "BASE", variationValues);
        Assert.Equal("Cobalt-Blue", sku1);

        // 2. Normalized match (ignores spaces/hyphens)
        var sku2 = SkuPatternResolver.GenerateSku("{primarycolor}", "BASE", variationValues);
        Assert.Equal("Cobalt-Blue", sku2);

        // 3. Suffix match ("color" matches "Primary Color")
        var sku3 = SkuPatternResolver.GenerateSku("{color}", "BASE", variationValues);
        Assert.Equal("Cobalt-Blue", sku3);

        // 4. Combined pattern with base SKU
        var combined = SkuPatternResolver.GenerateSku("{sku}-{size}-{color}", "SHIRT", variationValues);
        Assert.Equal("SHIRT-X-Large-Cobalt-Blue", combined);
    }

    [Fact]
    public void R4_VariationEditor_ReorderValues_2DGrid_ReordersProductsCanonically()
    {
        // 2D grid: Size (Small, Medium, Large) × Color (Red, Blue) = 6 products
        var sizes = new[] { "Small", "Medium", "Large" };
        var colors = new[] { "Red", "Blue" };

        var products = new JsonArray();
        long offeringId = 1000;
        foreach (var s in sizes)
        {
            foreach (var c in colors)
            {
                products.Add(new JsonObject
                {
                    ["property_values"] = new JsonArray(
                        new JsonObject
                        {
                            ["property_id"] = SizePropertyId,
                            ["property_name"] = "Size",
                            ["values"] = new JsonArray(s),
                            ["value_ids"] = new JsonArray(1L),
                        },
                        new JsonObject
                        {
                            ["property_id"] = ColorPropertyId,
                            ["property_name"] = "Color",
                            ["values"] = new JsonArray(c),
                            ["value_ids"] = new JsonArray(2L),
                        }),
                    ["offerings"] = new JsonArray(new JsonObject
                    {
                        ["offering_id"] = ++offeringId,
                        ["price_amount"] = 2500L,
                        ["price_divisor"] = 100,
                        ["quantity"] = 10,
                        ["is_enabled"] = true,
                    }),
                    ["sku"] = $"SKU-{s}-{c}",
                });
            }
        }

        var inv = new JsonObject
        {
            ["products"] = products,
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["value_order"] = new JsonObject
            {
                [SizePropertyId.ToString()] = new JsonArray("Small", "Medium", "Large"),
            },
        };

        // Reorder Color variation to [Blue, Red]
        var reordered = VariationEditor.ReorderValues(inv, ColorPropertyId, ["Blue", "Red"]);

        var reorderedProducts = (reordered["products"]!.AsArray()).Cast<JsonObject>().ToList();
        Assert.Equal(6, reorderedProducts.Count);

        // Canonical sequence: First dimension (Size: Small, Medium, Large), then Color (Blue, Red)
        var expectedCombinations = new[]
        {
            ("Small", "Blue"),
            ("Small", "Red"),
            ("Medium", "Blue"),
            ("Medium", "Red"),
            ("Large", "Blue"),
            ("Large", "Red"),
        };

        for (var i = 0; i < expectedCombinations.Length; i++)
        {
            var p = reorderedProducts[i];
            var pvs = p["property_values"]!.AsArray();
            var sizeVal = (string)pvs.First(v => (long)v!["property_id"]! == SizePropertyId)!["values"]![0]!;
            var colorVal = (string)pvs.First(v => (long)v!["property_id"]! == ColorPropertyId)!["values"]![0]!;
            Assert.Equal(expectedCombinations[i].Item1, sizeVal);
            Assert.Equal(expectedCombinations[i].Item2, colorVal);
        }
    }

    [Fact]
    public void R4_VariationEditor_ReorderValues_InvalidValues_ThrowsArgumentException()
    {
        var inv = new JsonObject
        {
            ["products"] = new JsonArray(
                new JsonObject
                {
                    ["property_values"] = new JsonArray(new JsonObject
                    {
                        ["property_id"] = ColorPropertyId,
                        ["property_name"] = "Color",
                        ["values"] = new JsonArray("Red"),
                    }),
                },
                new JsonObject
                {
                    ["property_values"] = new JsonArray(new JsonObject
                    {
                        ["property_id"] = ColorPropertyId,
                        ["property_name"] = "Color",
                        ["values"] = new JsonArray("Blue"),
                    }),
                }),
        };

        // 1. Missing a value (only "Red" passed when "Blue" also exists)
        Assert.Throws<ArgumentException>(() => VariationEditor.ReorderValues(inv, ColorPropertyId, ["Red"]));

        // 2. Extra alien value passed ("Green")
        Assert.Throws<ArgumentException>(() => VariationEditor.ReorderValues(inv, ColorPropertyId, ["Red", "Green"]));

        // 3. Property does not exist
        Assert.Throws<ArgumentException>(() => VariationEditor.ReorderValues(inv, 99999L, ["Red", "Blue"]));
    }

    [Fact]
    public void R4_InventoryWriter_FromLocal_CustomValueOrder_PreservesLocalGridOrderAndEtsyStock()
    {
        // Etsy inventory with products in order: (Small, Red), (Small, Blue)
        var etsyInventory = new EtsyInventory
        {
            PriceOnProperty = [],
            QuantityOnProperty = [],
            Products =
            [
                new EtsyProduct
                {
                    PropertyValues =
                    [
                        new EtsyPropertyValue { PropertyId = SizePropertyId, Values = ["Small"] },
                        new EtsyPropertyValue { PropertyId = ColorPropertyId, Values = ["Red"] },
                    ],
                    Offerings = [new EtsyOffering { OfferingId = 101, Quantity = 7, IsEnabled = true, Price = new EtsyMoney { Amount = 2000, Divisor = 100 } }],
                },
                new EtsyProduct
                {
                    PropertyValues =
                    [
                        new EtsyPropertyValue { PropertyId = SizePropertyId, Values = ["Small"] },
                        new EtsyPropertyValue { PropertyId = ColorPropertyId, Values = ["Blue"] },
                    ],
                    Offerings = [new EtsyOffering { OfferingId = 102, Quantity = 14, IsEnabled = true, Price = new EtsyMoney { Amount = 2000, Divisor = 100 } }],
                },
            ],
        };

        // Local inventory with custom value_order reversing Colors to [Blue, Red]
        var localInventory = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["value_order"] = new JsonObject
            {
                [ColorPropertyId.ToString()] = new JsonArray("Blue", "Red"),
            },
            ["products"] = new JsonArray(
                new JsonObject
                {
                    ["sku"] = "SKU-S-BLUE",
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = SizePropertyId, ["values"] = new JsonArray("Small"), ["value_ids"] = new JsonArray() },
                        new JsonObject { ["property_id"] = ColorPropertyId, ["values"] = new JsonArray("Blue"), ["value_ids"] = new JsonArray() }),
                    ["offerings"] = new JsonArray(new JsonObject { ["price_amount"] = 2000L, ["price_divisor"] = 100, ["is_enabled"] = true }),
                },
                new JsonObject
                {
                    ["sku"] = "SKU-S-RED",
                    ["property_values"] = new JsonArray(
                        new JsonObject { ["property_id"] = SizePropertyId, ["values"] = new JsonArray("Small"), ["value_ids"] = new JsonArray() },
                        new JsonObject { ["property_id"] = ColorPropertyId, ["values"] = new JsonArray("Red"), ["value_ids"] = new JsonArray() }),
                    ["offerings"] = new JsonArray(new JsonObject { ["price_amount"] = 2000L, ["price_divisor"] = 100, ["is_enabled"] = true }),
                }),
        };

        var update = InventoryWriter.FromLocal(etsyInventory, localInventory);

        // Sequence must match local product grid: Blue first, then Red
        Assert.Equal(2, update.Products.Count);
        Assert.Equal("SKU-S-BLUE", update.Products[0].Sku);
        Assert.Equal("SKU-S-RED", update.Products[1].Sku);

        // But Etsy stock must be preserved from Etsy's existing offerings:
        // Blue had quantity 14, Red had quantity 7
        Assert.Equal(14, update.Products[0].Offerings[0].Quantity);
        Assert.Equal(7, update.Products[1].Offerings[0].Quantity);
    }

    [Fact]
    public void R4_InventoryWriter_FromLocal_WithoutCustomValueOrder_PreservesEtsyProductOrder()
    {
        // Etsy inventory in order Product A, Product B
        var etsyInventory = new EtsyInventory
        {
            PriceOnProperty = [],
            QuantityOnProperty = [],
            Products =
            [
                new EtsyProduct
                {
                    PropertyValues = [new EtsyPropertyValue { PropertyId = ColorPropertyId, Values = ["Red"] }],
                    Offerings = [new EtsyOffering { Quantity = 5, IsEnabled = true, Price = new EtsyMoney { Amount = 1000, Divisor = 100 } }],
                },
                new EtsyProduct
                {
                    PropertyValues = [new EtsyPropertyValue { PropertyId = ColorPropertyId, Values = ["Blue"] }],
                    Offerings = [new EtsyOffering { Quantity = 8, IsEnabled = true, Price = new EtsyMoney { Amount = 1000, Divisor = 100 } }],
                },
            ],
        };

        // Local inventory lists Product B first, but NO value_order specified
        var localInventory = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(
                new JsonObject
                {
                    ["sku"] = "SKU-BLUE",
                    ["property_values"] = new JsonArray(new JsonObject { ["property_id"] = ColorPropertyId, ["values"] = new JsonArray("Blue"), ["value_ids"] = new JsonArray() }),
                    ["offerings"] = new JsonArray(new JsonObject { ["price_amount"] = 1000L, ["price_divisor"] = 100, ["is_enabled"] = true }),
                },
                new JsonObject
                {
                    ["sku"] = "SKU-RED",
                    ["property_values"] = new JsonArray(new JsonObject { ["property_id"] = ColorPropertyId, ["values"] = new JsonArray("Red"), ["value_ids"] = new JsonArray() }),
                    ["offerings"] = new JsonArray(new JsonObject { ["price_amount"] = 1000L, ["price_divisor"] = 100, ["is_enabled"] = true }),
                }),
        };

        var update = InventoryWriter.FromLocal(etsyInventory, localInventory);

        // When custom value_order is absent, Etsy's product order (Red first, then Blue) is preserved
        Assert.Equal(2, update.Products.Count);
        Assert.Equal("SKU-RED", update.Products[0].Sku);
        Assert.Equal("SKU-BLUE", update.Products[1].Sku);
    }
}
