using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Configuration;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>
/// Content-addressed image files next to the database: <c>images/ab/abcdef….jpg</c>, named by the SHA-256 of the
/// bytes. The same photo is stored once, a name never points at different content, and files can be served with
/// long cache lifetimes.
/// </summary>
public sealed partial class ImageStore(StorageOptions storage)
{
    public const string UrlPrefix = "/media/images/";

    [GeneratedRegex("^[0-9a-f]{64}\\.(jpg|png|gif|webp)$")]
    private static partial Regex FileNamePattern();

    public static bool IsValidFileName(string? fileName) => fileName is not null && FileNamePattern().IsMatch(fileName);

    public static string? UrlFor(string? fileName) => IsValidFileName(fileName) ? UrlPrefix + fileName : null;

    public string PathFor(string fileName)
    {
        if (!IsValidFileName(fileName)) throw new ArgumentException($"Invalid image file name '{fileName}'.", nameof(fileName));
        return Path.Combine(storage.ImagesDirectory, fileName[..2], fileName);
    }

    public bool Exists(string? fileName) => IsValidFileName(fileName) && File.Exists(PathFor(fileName!));

    /// <summary>Stores the bytes (if not already present) and returns the content-hashed file name.</summary>
    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct = default)
    {
        var fileName = FileNameFor(content, extension);
        var path = PathFor(fileName);
        if (File.Exists(path)) return fileName;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, path, overwrite: true); // same content either way, so a race is harmless
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return fileName;
    }

    public static string FileNameFor(byte[] content, string extension) =>
        Convert.ToHexStringLower(SHA256.HashData(content)) + "." + NormalizeExtension(extension);

    /// <summary>Picks the file extension from the response content type, falling back to the URL.</summary>
    public static string ExtensionFor(string? contentType, string? url)
    {
        var fromType = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" or "image/pjpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => null,
        };
        if (fromType is not null) return fromType;

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url ?? "";
        return NormalizeExtension(Path.GetExtension(path).TrimStart('.'));
    }

    private static string NormalizeExtension(string ext) => ext.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "jpg",
        "png" => "png",
        "gif" => "gif",
        "webp" => "webp",
        _ => "jpg",
    };

    /// <summary>Deletes stored files that no image row references, if older than <paramref name="minAge"/>.</summary>
    public int DeleteUnreferenced(IReadOnlySet<string> referenced, TimeSpan minAge, DateTime nowUtc)
    {
        if (!Directory.Exists(storage.ImagesDirectory)) return 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(storage.ImagesDirectory, "*", SearchOption.AllDirectories))
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
