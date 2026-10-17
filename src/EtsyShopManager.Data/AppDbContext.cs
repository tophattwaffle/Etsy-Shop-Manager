using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EtsyShopManager.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<ShopToken> ShopTokens => Set<ShopToken>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<EtsyWriteLog> EtsyWriteLogs => Set<EtsyWriteLog>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<RecurringJobState> RecurringJobs => Set<RecurringJobState>();

    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingEtsyState> ListingEtsyStates => Set<ListingEtsyState>();
    public DbSet<ListingImage> ListingImages => Set<ListingImage>();
    public DbSet<ListingProduct> ListingProducts => Set<ListingProduct>();
    public DbSet<ListingOffering> ListingOfferings => Set<ListingOffering>();
    public DbSet<ShopSyncState> ShopSyncStates => Set<ShopSyncState>();
    public DbSet<ListingPush> ListingPushes => Set<ListingPush>();
    public DbSet<ListingSnapshot> ListingSnapshots => Set<ListingSnapshot>();
    public DbSet<ListingRefill> ListingRefills => Set<ListingRefill>();

    public DbSet<ShippingProfile> ShippingProfiles => Set<ShippingProfile>();
    public DbSet<ProcessingProfile> ProcessingProfiles => Set<ProcessingProfile>();
    public DbSet<ReturnPolicy> ReturnPolicies => Set<ReturnPolicy>();
    public DbSet<ShopSection> ShopSections => Set<ShopSection>();
    public DbSet<ProductionPartner> ProductionPartners => Set<ProductionPartner>();
    public DbSet<TaxonomyNode> TaxonomyNodes => Set<TaxonomyNode>();
    public DbSet<TaxonomyProperty> TaxonomyProperties => Set<TaxonomyProperty>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ShopOrderSyncState> ShopOrderSyncStates => Set<ShopOrderSyncState>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Shop>(e =>
        {
            e.HasIndex(x => x.EtsyShopId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CurrencyCode).HasMaxLength(8);
            e.HasOne(x => x.Token).WithOne(t => t.Shop).HasForeignKey<ShopToken>(t => t.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShopToken>(e => e.HasKey(x => x.ShopId));

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
        });

        b.Entity<AuditEntry>(e => e.HasIndex(x => x.AtUtc));
        b.Entity<EtsyWriteLog>(e => e.HasIndex(x => x.AtUtc));

        b.Entity<Job>(e =>
        {
            e.Property(x => x.JobType).HasMaxLength(200);
            e.Property(x => x.DedupeKey).HasMaxLength(400);
            e.Property(x => x.Result).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.Status, x.RunAfterUtc, x.Priority });
            e.HasIndex(x => x.DedupeKey);
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<RecurringJobState>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        ConfigureListings(b);
        ConfigureLookups(b);
        ConfigureOrders(b);

        // SQLite has no DateTime kind; everything we store is UTC, so mark it as such on the way out.
        // (DateTimeOffset is avoided on purpose: EF's SQLite provider cannot order or compare it in SQL.)
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
            else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
        }
    }

    private static void ConfigureListings(ModelBuilder b)
    {
        b.Entity<Listing>(e =>
        {
            e.HasIndex(x => x.EtsyListingId).IsUnique();
            e.HasIndex(x => new { x.ShopId, x.State });
            e.HasIndex(x => x.Title);
            e.HasIndex(x => x.EtsyUpdatedAtUtc);
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(400);
            e.Property(x => x.CurrencyCode).HasMaxLength(8);
            e.Property(x => x.LastSeenRunId).HasMaxLength(64);
            e.Property(x => x.ItemWeight).HasConversion<double?>();
            e.Property(x => x.ItemLength).HasConversion<double?>();
            e.Property(x => x.ItemWidth).HasConversion<double?>();
            e.Property(x => x.ItemHeight).HasConversion<double?>();
            e.Property(x => x.Attributes).HasJsonConversion();
            e.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.EtsyState).WithOne(s => s.Listing).HasForeignKey<ListingEtsyState>(s => s.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Images).WithOne(i => i.Listing).HasForeignKey(i => i.ListingId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Products).WithOne(p => p.Listing).HasForeignKey(p => p.ListingId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ListingEtsyState>(e =>
        {
            e.HasKey(x => x.ListingId);
            e.Property(x => x.ManagedHash).HasMaxLength(64);
        });

        b.Entity<ListingImage>(e =>
        {
            // Unique per listing only: listings copied on Etsy share the same listing_image_id.
            e.HasIndex(x => new { x.ListingId, x.EtsyImageId }).IsUnique();
            e.HasIndex(x => x.EtsyImageId);
            e.HasIndex(x => new { x.ListingId, x.Rank });
            e.HasIndex(x => x.FileName);
            e.HasIndex(x => x.ThumbFileName);
            e.Property(x => x.FileName).HasMaxLength(100);
            e.Property(x => x.ThumbFileName).HasMaxLength(100);
        });

        b.Entity<ListingProduct>(e =>
        {
            e.HasIndex(x => x.ListingId);
            e.HasIndex(x => x.Sku);
            e.Property(x => x.PropertyValues).HasJsonConversion();
            e.HasMany(x => x.Offerings).WithOne(o => o.Product).HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ListingOffering>(e => e.HasIndex(x => x.ProductId));

        b.Entity<Listing>(e =>
        {
            e.HasIndex(x => x.HasDraft);
            e.HasIndex(x => x.HasDrift);
        });

        b.Entity<ListingPush>(e =>
        {
            e.HasIndex(x => new { x.ListingId, x.RequestedAtUtc });
            e.Property(x => x.Status).HasConversion<int>();
            e.HasOne(x => x.Listing).WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Snapshot).WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ListingSnapshot>(e =>
        {
            e.HasIndex(x => new { x.ListingId, x.TakenAtUtc });
            e.HasOne(x => x.Listing).WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ListingRefill>(e =>
        {
            e.HasKey(x => x.ListingId);
            e.HasIndex(x => x.Enabled);
            e.HasOne(x => x.Listing).WithOne().HasForeignKey<ListingRefill>(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShopSyncState>(e =>
        {
            e.HasKey(x => x.ShopId);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.HasOne(x => x.Shop).WithOne().HasForeignKey<ShopSyncState>(x => x.ShopId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLookups(ModelBuilder b)
    {
        b.Entity<ShippingProfile>(e => e.HasIndex(x => new { x.ShopId, x.EtsyShippingProfileId }).IsUnique());
        b.Entity<ProcessingProfile>(e => e.HasIndex(x => new { x.ShopId, x.ReadinessStateId }).IsUnique());
        b.Entity<ReturnPolicy>(e => e.HasIndex(x => new { x.ShopId, x.EtsyReturnPolicyId }).IsUnique());
        b.Entity<ShopSection>(e => e.HasIndex(x => new { x.ShopId, x.EtsySectionId }).IsUnique());
        b.Entity<ProductionPartner>(e => e.HasIndex(x => new { x.ShopId, x.EtsyPartnerId }).IsUnique());

        foreach (var type in new[] { typeof(ShippingProfile), typeof(ProcessingProfile), typeof(ReturnPolicy),
                     typeof(ShopSection), typeof(ProductionPartner) })
        {
            b.Entity(type).HasOne(typeof(Shop)).WithMany().HasForeignKey("ShopId").OnDelete(DeleteBehavior.Cascade);
        }

        b.Entity<TaxonomyNode>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.ParentId);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<TaxonomyProperty>(e => e.HasKey(x => new { x.TaxonomyId, x.PropertyId }));
    }

    private static void ConfigureOrders(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.HasIndex(x => x.EtsyReceiptId).IsUnique();
            e.HasIndex(x => new { x.ShopId, x.IsPaid, x.IsShipped });
            e.HasIndex(x => x.EtsyCreatedAtUtc);
            e.Property(x => x.Status).HasMaxLength(40);
            e.Property(x => x.CurrencyCode).HasMaxLength(8);
            e.HasOne(x => x.Shop).WithMany().HasForeignKey(x => x.ShopId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderItem>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.EtsyTransactionId }).IsUnique();
            e.HasIndex(x => x.EtsyListingId);
            e.Property(x => x.Variations).HasJsonConversion();
        });

        b.Entity<ShopOrderSyncState>(e =>
        {
            e.HasKey(x => x.ShopId);
            e.HasOne(x => x.Shop).WithOne().HasForeignKey<ShopOrderSyncState>(x => x.ShopId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.Hash).HasMaxLength(64);
        });
    }
}
