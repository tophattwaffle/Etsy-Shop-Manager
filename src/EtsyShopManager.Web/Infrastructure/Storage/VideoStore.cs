using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Configuration;

namespace EtsyShopManager.Web.Infrastructure.Storage;

/// <summary>
/// Content-addressed video files next to the database: <c>videos/ab/abcdef….mp4</c>, named by the SHA-256 of the
/// bytes. Supports Range requests for HTML5 playback and long cache lifetimes.
/// </summary>
public sealed partial class VideoStore(StorageOptions storage)
{
    public const string UrlPrefix = "/media/videos/";

    [GeneratedRegex("^[0-9a-f]{64}\\.(mp4|mov)$")]
    private static partial Regex FileNamePattern();

    public static bool IsValidFileName(string? fileName) => fileName is not null && FileNamePattern().IsMatch(fileName);

    public static string? UrlFor(string? fileName) => IsValidFileName(fileName) ? UrlPrefix.TrimStart('/') + fileName : null;

    public string PathFor(string fileName)
    {
        if (!IsValidFileName(fileName)) throw new ArgumentException($"Invalid video file name '{fileName}'.", nameof(fileName));
        return Path.Combine(storage.VideosDirectory, fileName[..2], fileName);
    }

    public bool Exists(string? fileName) => IsValidFileName(fileName) && File.Exists(PathFor(fileName!));

    public static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };

    public static string ExtensionFor(string? contentType, string? url)
    {
        var fromType = contentType?.ToLowerInvariant() switch
        {
            "video/quicktime" => "mov",
            "video/mp4" => "mp4",
            _ => null,
        };
        if (fromType is not null) return fromType;

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url ?? "";
        return NormalizeExtension(Path.GetExtension(path));
    }

    public static string NormalizeExtension(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "mov" or "quicktime" => "mov",
        _ => "mp4",
    };

    /// <summary>Stores the stream content (if not already present) and returns the content-hashed file name.</summary>
    public string Save(Stream content, string extension)
    {
        using var sha256 = SHA256.Create();
        using var mem = new MemoryStream();
        content.CopyTo(mem);
        var bytes = mem.ToArray();
        var hash = Convert.ToHexStringLower(sha256.ComputeHash(bytes));
        var fileName = hash + "." + NormalizeExtension(extension);
        var path = PathFor(fileName);
        if (File.Exists(path)) return fileName;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return fileName;
    }

    /// <summary>Stores the stream content asynchronously and returns the content-hashed file name.</summary>
    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        using var mem = new MemoryStream();
        await content.CopyToAsync(mem, ct);
        var bytes = mem.ToArray();
        return await SaveAsync(bytes, extension, ct);
    }

    /// <summary>Stores the bytes (if not already present) and returns the content-hashed file name.</summary>
    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct = default)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var fileName = hash + "." + NormalizeExtension(extension);
        var path = PathFor(fileName);
        if (File.Exists(path)) return fileName;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return fileName;
    }

    /// <summary>Deletes stored files that no video row references, if older than <paramref name="minAge"/>.</summary>
    public int DeleteUnreferenced(IReadOnlySet<string> referenced, TimeSpan minAge, DateTime nowUtc)
    {
        if (!Directory.Exists(storage.VideosDirectory)) return 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(storage.VideosDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                if (File.GetLastWriteTimeUtc(path) < nowUtc - TimeSpan.FromHours(1)) File.Delete(path);
                continue;
            }
            if (!IsValidFileName(name) || referenced.Contains(name)) continue;
            if (File.GetLastWriteTimeUtc(path) > nowUtc - minAge) continue;
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }
}
