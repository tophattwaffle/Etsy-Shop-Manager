namespace EtsyShopManager.Web.Infrastructure.Usps;

public interface IUspsClient
{
    /// <summary>
    /// Gets a valid OAuth Bearer access token using the provided credentials, caching it in memory until near expiry.
    /// </summary>
    Task<string> GetAccessTokenAsync(string consumerKey, string consumerSecret, CancellationToken ct = default);

    /// <summary>
    /// Standardizes and checks delivery point validation for the given US address with USPS Addresses API v3.
    /// Throws <see cref="EtsyShopManager.Core.Jobs.RetryLaterException"/> on HTTP 429.
    /// </summary>
    Task<UspsAddressCheckResult> StandardizeAddressAsync(
        UspsAddressRequest request,
        string consumerKey,
        string consumerSecret,
        CancellationToken ct = default);

    /// <summary>
    /// Tests the connection to the USPS Addresses API with a known US address and returns diagnostic result.
    /// </summary>
    Task<UspsConnectionTestResult> TestConnectionAsync(
        string consumerKey,
        string consumerSecret,
        CancellationToken ct = default);
}
