using System.Globalization;
using System.Reflection;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>
/// The running build: version and git commit (the SDK stamps "0.1.0+&lt;sha&gt;" into the informational version when
/// building from a git checkout, as the deploy runner does) plus the build time from the project file.
/// </summary>
public static class AppVersion
{
    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    public static string Version { get; }
    public static string? Commit { get; }
    public static DateTime? BuiltAtUtc { get; }

    /// <summary>"0.1.0 · 52a9219", or just the version when the commit is unknown.</summary>
    public static string Display => Commit is null ? Version : $"{Version} · {ShortCommit}";

    public static string? ShortCommit => Commit is { Length: > 7 } c ? c[..7] : Commit;

    static AppVersion()
    {
        var info = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? Assembly.GetName().Version?.ToString() ?? "0.0.0";
        (Version, Commit) = Parse(info);

        var built = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value;
        if (DateTime.TryParse(built, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            BuiltAtUtc = at;
    }

    internal static (string Version, string? Commit) Parse(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0
            ? (informationalVersion, null)
            : (informationalVersion[..plus], informationalVersion[(plus + 1)..] is { Length: > 0 } sha ? sha : null);
    }
}
