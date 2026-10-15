using EtsyShopManager.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtsyShopManager.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers AppDbContext both as a scoped service (Identity, request code) and through
    /// IDbContextFactory (Blazor components, background services and singletons).
    /// </summary>
    public static IServiceCollection AddAppData(this IServiceCollection services, StorageOptions storage)
    {
        void Configure(DbContextOptionsBuilder o) =>
            o.UseSqlite(storage.ConnectionString, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));

        services.AddDbContext<AppDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
        services.AddDbContextFactory<AppDbContext>(Configure);
        return services;
    }

    /// <summary>Applies migrations and enables WAL. Call once at startup, before the app serves requests.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        if (!db.Database.GetMigrations().Any())
        {
            throw new InvalidOperationException(
                "No EF Core migrations were found in EtsyShopManager.Data. Run scripts\\build.ps1 once " +
                "(it creates the InitialCreate migration), or: dotnet ef migrations add InitialCreate " +
                "-p src/EtsyShopManager.Data -s src/EtsyShopManager.Web -o Migrations");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
        }

        // WAL lets the UI read while background jobs write. The setting is stored in the database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        SqliteConnection.ClearAllPools();
    }
}
