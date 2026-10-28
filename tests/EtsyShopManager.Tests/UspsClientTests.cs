using System.Net;
using System.Net.Http.Headers;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Infrastructure.Usps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

public class UspsClientTests
{
    private static (UspsClient Client, ScriptedHandler Handler, ImmediateTimeProvider Time) CreateClient(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var http = new HttpClient(handler);
        var options = Options.Create(new UspsOptions
        {
            ApiBaseUrl = "https://apis.usps.com",
            OAuthUrl = "https://apis.usps.com/oauth2/v3/token"
        });
        var time = new ImmediateTimeProvider();
        var client = new UspsClient(http, options, time, NullLogger<UspsClient>.Instance);
        return (client, handler, time);
    }

    [Fact]
    public async Task GetAccessToken_caches_token_and_reuses_it_until_near_expiry()
    {
        var tokenCount = 0;
        var (client, handler, time) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                tokenCount++;
                return ScriptedHandler.Json(HttpStatusCode.OK, $$"""
                {
                    "access_token": "token_{{tokenCount}}",
                    "token_type": "Bearer",
                    "expires_in": 28800
                }
                """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // 1. Initial token fetch
        var token1 = await client.GetAccessTokenAsync("key1", "secret1");
        Assert.Equal("token_1", token1);
        Assert.Single(handler.Requests);

        // 2. Immediate second call reuses cached token (no new HTTP request)
        var token2 = await client.GetAccessTokenAsync("key1", "secret1");
        Assert.Equal("token_1", token2);
        Assert.Single(handler.Requests);

        // 3. Advance time by 7 hours (still within 8h - 5m buffer) -> still cached
        time.Now = time.Now.AddHours(7);
        var token3 = await client.GetAccessTokenAsync("key1", "secret1");
        Assert.Equal("token_1", token3);
        Assert.Single(handler.Requests);

        // 4. Advance time past the 5-minute safety margin (e.g. 7 hours 56 minutes from start)
        time.Now = time.Now.AddMinutes(56);
        var token4 = await client.GetAccessTokenAsync("key1", "secret1");
        Assert.Equal("token_2", token4);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAccessToken_fetches_new_token_when_credentials_change()
    {
        var tokenCount = 0;
        var (client, handler, _) = CreateClient((req, call) =>
        {
            tokenCount++;
            return ScriptedHandler.Json(HttpStatusCode.OK, $$"""
            {
                "access_token": "token_{{tokenCount}}",
                "token_type": "Bearer",
                "expires_in": 28800
            }
            """);
        });

        var token1 = await client.GetAccessTokenAsync("key1", "secret1");
        Assert.Equal("token_1", token1);

        var token2 = await client.GetAccessTokenAsync("key2", "secret2");
        Assert.Equal("token_2", token2);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("key", "")]
    [InlineData("   ", "   ")]
    public async Task GetAccessToken_throws_when_keys_are_empty(string key, string secret)
    {
        var (client, _, _) = CreateClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccessTokenAsync(key, secret));
    }

    [Fact]
    public async Task GetAccessToken_throws_RetryLaterException_on_HTTP_429()
    {
        var (client, _, _) = CreateClient((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(150));
            return resp;
        });

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() => client.GetAccessTokenAsync("key", "secret"));
        Assert.Equal(TimeSpan.FromSeconds(150), ex.Delay);
    }

    [Fact]
    public async Task StandardizeAddress_parses_nested_response_and_DPV_details()
    {
        var (client, handler, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                {
                    "access_token": "valid_token",
                    "token_type": "Bearer",
                    "expires_in": 28800
                }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "address": {
                    "streetAddress": "1600 PENNSYLVANIA AVE NW",
                    "secondaryAddress": null,
                    "city": "WASHINGTON",
                    "state": "DC",
                    "ZIPCode": "20500",
                    "ZIPPlus4": "0005"
                },
                "additionalInfo": {
                    "DPVConfirmation": "Y",
                    "DPVFootnotes": "AABB"
                }
            }
            """);
        });

        var req = new UspsAddressRequest("1600 Pennsylvania Ave NW", null, "Washington", "DC", "20500");
        var result = await client.StandardizeAddressAsync(req, "key", "secret");

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Response);
        Assert.Equal("1600 PENNSYLVANIA AVE NW", result.Response.EffectiveStreetAddress);
        Assert.Equal("WASHINGTON", result.Response.EffectiveCity);
        Assert.Equal("DC", result.Response.EffectiveState);
        Assert.Equal("20500", result.Response.EffectiveZIPCode);
        Assert.Equal("0005", result.Response.EffectiveZIPPlus4);
        Assert.Equal("Y", result.Response.EffectiveDPVConfirmation);
        Assert.Equal("AABB", result.Response.EffectiveDPVFootnotes);
    }

    [Fact]
    public async Task StandardizeAddress_parses_flat_response_structure()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "valid_token", "expires_in": 28800 }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "streetAddress": "123 MAIN ST",
                "secondaryAddress": "APT 4B",
                "city": "SPRINGFIELD",
                "state": "IL",
                "ZIPCode": "62704",
                "ZIPPlus4": "1234",
                "DPVConfirmation": "D",
                "DPVFootnotes": "AAN1"
            }
            """);
        });

        var req = new UspsAddressRequest("123 Main St", "Apt 4B", "Springfield", "IL", "62704");
        var result = await client.StandardizeAddressAsync(req, "key", "secret");

        Assert.True(result.Success);
        Assert.Equal("123 MAIN ST", result.Response!.EffectiveStreetAddress);
        Assert.Equal("APT 4B", result.Response.EffectiveSecondaryAddress);
        Assert.Equal("SPRINGFIELD", result.Response.EffectiveCity);
        Assert.Equal("IL", result.Response.EffectiveState);
        Assert.Equal("62704", result.Response.EffectiveZIPCode);
        Assert.Equal("1234", result.Response.EffectiveZIPPlus4);
        Assert.Equal("D", result.Response.EffectiveDPVConfirmation);
    }

    [Fact]
    public async Task StandardizeAddress_throws_RetryLaterException_on_HTTP_429()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "valid_token", "expires_in": 28800 }
                """);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return resp;
        });

        var req = new UspsAddressRequest("123 Main St", null, "Springfield", "IL", "62704");
        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.StandardizeAddressAsync(req, "key", "secret"));
        Assert.Equal(TimeSpan.FromSeconds(300), ex.Delay);
    }

    [Fact]
    public async Task StandardizeAddress_clears_token_on_HTTP_401()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "stale_token", "expires_in": 28800 }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.Unauthorized, """
            { "error": { "message": "Invalid token" } }
            """);
        });

        var req = new UspsAddressRequest("123 Main St", null, "Springfield", "IL", "62704");
        var result = await client.StandardizeAddressAsync(req, "key", "secret");

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task StandardizeAddress_handles_HTTP_404_not_found()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "valid_token", "expires_in": 28800 }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.NotFound, """
            {
                "apiVersion": "/addresses/v3/",
                "error": {
                    "code": "404",
                    "message": "Address Not Found.",
                    "errors": [
                        { "title": "Address Not Found", "detail": "The address specified was not found in the USPS database." }
                    ]
                }
            }
            """);
        });

        var req = new UspsAddressRequest("9999 Nonexistent St", null, "Nowhere", "AK", "99999");
        var result = await client.StandardizeAddressAsync(req, "key", "secret");

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("Address Not Found", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnection_returns_success_when_address_confirmed()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "valid_token", "expires_in": 28800 }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.OK, """
            {
                "address": { "streetAddress": "475 LENFANT PLZ SW", "city": "WASHINGTON", "state": "DC", "ZIPCode": "20260" },
                "additionalInfo": { "DPVConfirmation": "Y" }
            }
            """);
        });

        var test = await client.TestConnectionAsync("key", "secret");
        Assert.True(test.Success);
        Assert.Contains("DPV confirmed", test.Message);
    }

    [Fact]
    public async Task TestConnection_returns_descriptive_error_on_HTTP_401()
    {
        var (client, _, _) = CreateClient((_, _) =>
            ScriptedHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""));

        var test = await client.TestConnectionAsync("bad_key", "bad_secret");
        Assert.False(test.Success);
        Assert.Equal(401, test.StatusCode);
        Assert.Contains("Consumer Key", test.Message);
    }

    [Fact]
    public async Task TestConnection_returns_descriptive_error_on_HTTP_403()
    {
        var (client, _, _) = CreateClient((req, call) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "token_no_license", "expires_in": 28800 }
                """);
            }

            return ScriptedHandler.Json(HttpStatusCode.Forbidden, """{"error":"unauthorized_client"}""");
        });

        var test = await client.TestConnectionAsync("key", "secret");
        Assert.False(test.Success);
        Assert.Equal(403, test.StatusCode);
        Assert.Contains("license agreement", test.Message, StringComparison.OrdinalIgnoreCase);
    }
}
