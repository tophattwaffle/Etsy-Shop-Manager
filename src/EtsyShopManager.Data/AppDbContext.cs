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
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<RecurringJobState> RecurringJobs => Set<RecurringJobState>();

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

        b.Entity<Job>(e =>
        {
            e.Property(x => x.JobType).HasMaxLength(200);
            e.Property(x => x.DedupeKey).HasMaxLength(400);
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
}
