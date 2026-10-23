namespace EtsyShopManager.Core.Configuration;

/// <summary>
/// Where live data lives. Defaults to C:\ProgramData\EtsyShopManager. Must not be inside Dropbox
/// (sync can corrupt SQLite), which <see cref="Validate"/> enforces.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root folder. Empty means the default under CommonApplicationData.</summary>
    public string? DataRoot { get; set; }

    public string ResolvedRoot =>
        string.IsNullOrWhiteSpace(DataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EtsyShopManager")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataRoot));

    public string DatabasePath => Path.Combine(ResolvedRoot, "etsyshopmanager.db");
    public string ImagesDirectory => Path.Combine(ResolvedRoot, "images");
    public string VideosDirectory => Path.Combine(ResolvedRoot, "videos");
    public string KeysDirectory => Path.Combine(ResolvedRoot, "keys");
    public string BackupsDirectory => Path.Combine(ResolvedRoot, "backups");
    public string LogsDirectory => Path.Combine(ResolvedRoot, "logs");

    public string ConnectionString => $"Data Source={DatabasePath};Default Timeout=30;Pooling=True";

    public void Validate()
    {
        var root = ResolvedRoot;
        var parts = root.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase)
                           || p.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                           || p.StartsWith("OneDrive -", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Storage:DataRoot '{root}' is inside a synced folder (Dropbox/OneDrive). " +
                "File sync can corrupt the SQLite database. Use a local path such as C:\\ProgramData\\EtsyShopManager.");
        }
    }

    public void EnsureDirectories()
    {
        foreach (var dir in new[] { ResolvedRoot, ImagesDirectory, VideosDirectory, KeysDirectory, BackupsDirectory, LogsDirectory })
            Directory.CreateDirectory(dir);
    }
}
