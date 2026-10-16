using EtsyShopManager.Core.Configuration;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Jobs;

namespace EtsyShopManager.Tests;

public class UrlHelperTests
{
    [Theory]
    [InlineData("https://etsy.example.lan", "https://etsy.example.lan")]
    [InlineData("https://etsy.example.lan/", "https://etsy.example.lan")]
    [InlineData(" https://Etsy.Example.lan:8443/esm/ ", "https://etsy.example.lan:8443/esm")]
    public void Normalizes_base_url(string input, string expected)
    {
        Assert.True(UrlHelpers.TryNormalizeBaseUrl(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected + "/auth/etsy/callback", UrlHelpers.EtsyRedirectUri(normalized));
    }

    [Theory]
    [InlineData("http://etsy.example.lan")]
    [InlineData("etsy.example.lan")]
    [InlineData("https://etsy.example.lan/?x=1")]
    [InlineData("")]
    public void Rejects_invalid_base_url(string input) =>
        Assert.False(UrlHelpers.TryNormalizeBaseUrl(input, out _, out _));

    [Theory]
    [InlineData("/shops", true)]
    [InlineData("/", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData(null, false)]
    public void Detects_local_urls(string? url, bool expected) => Assert.Equal(expected, UrlHelpers.IsLocalUrl(url));
}

public class StorageOptionsTests
{
    [Theory]
    [InlineData(@"C:\Users\tophattwaffle\Dropbox\EtsyShopManager")]
    [InlineData("/home/tophattwaffle/Dropbox (Personal)/esm")]
    [InlineData("/home/tophattwaffle/OneDrive/esm")]
    public void Rejects_synced_folders(string root) =>
        Assert.Throws<InvalidOperationException>(() => new StorageOptions { DataRoot = root }.Validate());

    [Fact]
    public void Accepts_local_folder() => new StorageOptions { DataRoot = Path.GetTempPath() }.Validate();
}

public class JobBackoffTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(20, 3600)]
    public void Backoff_doubles_and_caps_at_an_hour(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), JobProcessor.Backoff(attempt));
}

public class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0+52a92198648dd3de4bbd0979aa4427176d0879e1", "0.1.0", "52a92198648dd3de4bbd0979aa4427176d0879e1")]
    [InlineData("0.1.0", "0.1.0", null)]
    [InlineData("0.1.0+", "0.1.0", null)]
    public void Splits_version_and_commit(string info, string version, string? commit) =>
        Assert.Equal((version, commit), AppVersion.Parse(info));

    [Fact]
    public void Build_from_git_carries_commit_and_build_time()
    {
        Assert.NotNull(AppVersion.Commit);
        Assert.StartsWith(AppVersion.Version + " · ", AppVersion.Display);
        Assert.NotNull(AppVersion.BuiltAtUtc);
    }
}
