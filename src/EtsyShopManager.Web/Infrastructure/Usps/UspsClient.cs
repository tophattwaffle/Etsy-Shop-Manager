using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EtsyShopManager.Core.Jobs;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Web.Infrastructure.Usps;

public class UspsClient : IUspsClient
{
    private readonly HttpClient _http;
    private readonly IOptions<UspsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UspsClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry = DateTimeOffset.MinValue;
    private string? _cachedKey;
    private string? _cachedSecret;

    // Safety buffer before actual expiry to refresh token
    private static readonly TimeSpan ExpirySafetyBuffer = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UspsClient(
        HttpClient http,
        IOptions<UspsOptions> options,
        TimeProvider time,
        ILogger<UspsClient> logger)
    {
        _http = http;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(string consumerKey, string consumerSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consumerKey) || string.IsNullOrWhiteSpace(consumerSecret))
            throw new InvalidOperationException("USPS Consumer Key and Consumer Secret must not be empty.");

        var now = _time.GetUtcNow();

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null
                && _cachedKey == consumerKey
                && _cachedSecret == consumerSecret
                && now < _cachedTokenExpiry - ExpirySafetyBuffer)
            {
                return _cachedToken;
            }

            var oauthUrl = _options.Value.OAuthUrl;
            if (string.IsNullOrWhiteSpace(oauthUrl))
                oauthUrl = "https://apis.usps.com/oauth2/v3/token";

            var requestBody = new UspsTokenRequest(consumerKey, consumerSecret);
            using var req = new HttpRequestMessage(HttpMethod.Post, oauthUrl)
            {
                Content = JsonContent.Create(requestBody)
            };

            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = ParseRetryAfter(resp.Headers.RetryAfter);
                throw new RetryLaterException($"USPS OAuth token rate limit reached (HTTP 429). Retry after {delay.TotalSeconds}s", delay);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("USPS OAuth token request failed with status {StatusCode}: {ErrorBody}", resp.StatusCode, errorBody);
                throw new HttpRequestException($"USPS authentication failed with HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}. Details: {errorBody}", null, resp.StatusCode);
            }

            var tokenResp = await resp.Content.ReadFromJsonAsync<UspsTokenResponse>(JsonOptions, ct)
                ?? throw new InvalidOperationException("Failed to deserialize USPS token response.");

            if (string.IsNullOrWhiteSpace(tokenResp.AccessToken))
                throw new InvalidOperationException("USPS token response did not contain an access_token.");

            _cachedToken = tokenResp.AccessToken;
            var expiresInSec = tokenResp.ExpiresIn > 0 ? tokenResp.ExpiresIn : 28800;
            _cachedTokenExpiry = now.AddSeconds(expiresInSec);
            _cachedKey = consumerKey;
            _cachedSecret = consumerSecret;

            _logger.LogInformation("Acquired fresh USPS access token (valid until {ExpiryUtc})", _cachedTokenExpiry);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<UspsAddressCheckResult> StandardizeAddressAsync(
        UspsAddressRequest request,
        string consumerKey,
        string consumerSecret,
        CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(consumerKey, consumerSecret, ct);

        var baseUrl = _options.Value.ApiBaseUrl?.TrimEnd('/') ?? "https://apis.usps.com";
        var endpoint = $"{baseUrl}/addresses/v3/address";

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = ParseRetryAfter(resp.Headers.RetryAfter);
            throw new RetryLaterException($"USPS Addresses API rate limit reached (HTTP 429). Retry after {delay.TotalSeconds}s", delay);
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token may have been invalidated or revoked; clear cache
            await ClearCachedTokenAsync();
            return new UspsAddressCheckResult(
                Success: false,
                StatusCode: (int)resp.StatusCode,
                Response: null,
                RawResponse: await resp.Content.ReadAsStringAsync(ct),
                ErrorMessage: "USPS authentication failed (HTTP 401). Token was invalidated.");
        }

        var rawContent = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode)
        {
            try
            {
                var addressResponse = JsonSerializer.Deserialize<UspsAddressResponse>(rawContent, JsonOptions);
                return new UspsAddressCheckResult(
                    Success: true,
                    StatusCode: (int)resp.StatusCode,
                    Response: addressResponse,
                    RawResponse: rawContent,
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize successful USPS address response: {Raw}", rawContent);
                return new UspsAddressCheckResult(
                    Success: false,
                    StatusCode: (int)resp.StatusCode,
                    Response: null,
                    RawResponse: rawContent,
                    ErrorMessage: $"Failed to parse USPS response: {ex.Message}");
            }
        }

        // Handle 400, 404, or other error statuses
        string? errorMessage = null;
        try
        {
            var errorResp = JsonSerializer.Deserialize<UspsErrorResponse>(rawContent, JsonOptions);
            if (errorResp?.Error is { } err)
            {
                var details = err.Errors is { Count: > 0 }
                    ? string.Join("; ", err.Errors.Select(e => $"{e.Title}: {e.Detail}"))
                    : err.Message;
                errorMessage = details ?? err.Message;
            }
        }
        catch
        {
            // Fall back to raw content if error structure isn't JSON
        }

        errorMessage ??= $"USPS API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";

        return new UspsAddressCheckResult(
            Success: false,
            StatusCode: (int)resp.StatusCode,
            Response: null,
            RawResponse: rawContent,
            ErrorMessage: errorMessage);
    }

    public async Task<UspsConnectionTestResult> TestConnectionAsync(
        string consumerKey,
        string consumerSecret,
        CancellationToken ct = default)
    {
        try
        {
            // Test address: USPS Headquarters
            var testAddress = new UspsAddressRequest(
                StreetAddress: "475 L'Enfant Plaza SW",
                SecondaryAddress: null,
                City: "Washington",
                State: "DC",
                ZipCode: "20260");

            var result = await StandardizeAddressAsync(testAddress, consumerKey, consumerSecret, ct);

            if (result.Success && result.Response is { } addr)
            {
                var dpv = addr.EffectiveDPVConfirmation;
                if (dpv == "Y")
                {
                    return new UspsConnectionTestResult(
                        Success: true,
                        Message: "Connection successful! USPS verified the test address (DPV confirmed).",
                        StatusCode: result.StatusCode);
                }

                return new UspsConnectionTestResult(
                    Success: true,
                    Message: $"Connection successful! USPS responded with DPV confirmation '{dpv}'.",
                    StatusCode: result.StatusCode,
                    Details: result.RawResponse);
            }

            if (result.StatusCode == 401)
            {
                return new UspsConnectionTestResult(
                    Success: false,
                    Message: "Authentication failed (HTTP 401). Please verify your USPS Consumer Key and Consumer Secret.",
                    StatusCode: 401,
                    Details: result.RawResponse);
            }

            if (result.StatusCode == 403)
            {
                return new UspsConnectionTestResult(
                    Success: false,
                    Message: "Access forbidden (HTTP 403). Ensure you have subscribed to the Addresses 3.0 API and signed the license agreement in the USPS Developer Portal.",
                    StatusCode: 403,
                    Details: result.RawResponse);
            }

            if (result.StatusCode == 429)
            {
                return new UspsConnectionTestResult(
                    Success: false,
                    Message: "Rate limit reached (HTTP 429). Please wait before trying again.",
                    StatusCode: 429,
                    Details: result.RawResponse);
            }

            return new UspsConnectionTestResult(
                Success: false,
                Message: result.ErrorMessage ?? $"USPS returned status {result.StatusCode}",
                StatusCode: result.StatusCode,
                Details: result.RawResponse);
        }
        catch (RetryLaterException rle)
        {
            return new UspsConnectionTestResult(
                Success: false,
                Message: $"Rate limit reached: {rle.Message}",
                StatusCode: 429);
        }
        catch (HttpRequestException hex) when (hex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new UspsConnectionTestResult(
                Success: false,
                Message: "Authentication failed (HTTP 401). Please verify your USPS Consumer Key and Consumer Secret.",
                StatusCode: 401,
                Details: hex.Message);
        }
        catch (HttpRequestException hex) when (hex.StatusCode == HttpStatusCode.Forbidden)
        {
            return new UspsConnectionTestResult(
                Success: false,
                Message: "Access forbidden (HTTP 403). Ensure you have subscribed to the Addresses 3.0 API and signed the license agreement in the USPS Developer Portal.",
                StatusCode: 403,
                Details: hex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USPS test connection failed with exception");
            return new UspsConnectionTestResult(
                Success: false,
                Message: $"Connection failed: {ex.Message}",
                StatusCode: null,
                Details: ex.ToString());
        }
    }

    private async Task ClearCachedTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            _cachedToken = null;
            _cachedTokenExpiry = DateTimeOffset.MinValue;
            _cachedKey = null;
            _cachedSecret = null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
            return TimeSpan.FromSeconds(60);

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var diff = date - _time.GetUtcNow();
            if (diff > TimeSpan.Zero)
                return diff;
        }

        return TimeSpan.FromSeconds(60);
    }
}
