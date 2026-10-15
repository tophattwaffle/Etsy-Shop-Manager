using System.Net;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

public class PkceTests
{
    [Fact]
    public void Challenge_matches_RFC7636_example()
    {
        // RFC 7636 Appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Verifier_is_valid_length_and_charset()
    {
        var v = Pkce.CreateVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.True(v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'));
    }

    [Fact]
    public void Token_prefix_is_user_id()
    {
        Assert.Equal(12345678L, EtsyTokenParser.TryGetUserId("12345678.abcDEF-ghi"));
        Assert.Null(EtsyTokenParser.TryGetUserId("no-prefix"));
        Assert.Null(EtsyTokenParser.TryGetUserId(null));
    }
}

public class RetryHandlerTests
{
    private static (HttpClient Client, ScriptedHandler Inner, ImmediateTimeProvider Time) Build(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond, int maxRetries = 3)
    {
        var time = new ImmediateTimeProvider();
        var inner = new ScriptedHandler(respond);
        var handler = new EtsyRetryHandler(Options.Create(new EtsyOptions { MaxRetries = maxRetries }), time,
            NullLogger<EtsyRetryHandler>.Instance) { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.etsy.com") }, inner, time);
    }

    [Fact]
    public async Task Retries_429_and_honours_Retry_After()
    {
        var (client, inner, time) = Build((_, n) =>
        {
            if (n == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await client.GetAsync("/v3/application/shops/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(2), time.Delays);
    }

    [Fact]
    public async Task Does_not_retry_5xx_for_POST()
    {
        var (client, inner, _) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var response = await client.PostAsync("/v3/application/shops/1/listings", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_for_GET()
    {
        var (client, inner, _) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway), maxRetries: 2);

        var response = await client.GetAsync("/v3/application/shops/1");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count); // first try + 2 retries
    }

    [Fact]
    public async Task Long_Retry_After_is_returned_instead_of_waited()
    {
        var (client, inner, _) = Build((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return r;
        });

        var response = await client.GetAsync("/v3/application/shops/1");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(inner.Requests);
    }
}

public class EtsyApiClientTests
{
    private sealed record Harness(ServiceProvider Provider, ScriptedHandler Http, InMemoryTokenStore Store,
        RecordingPublisher Events, ImmediateTimeProvider Time)
    {
        public EtsyApiClient Api => Provider.GetRequiredService<EtsyApiClient>();
        public EtsyTokenService Tokens => Provider.GetRequiredService<EtsyTokenService>();
    }

    private static Harness Build(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var http = new ScriptedHandler(respond);
        var store = new InMemoryTokenStore();
        var events = new RecordingPublisher();
        var time = new ImmediateTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IEtsyCredentialsProvider, FakeCredentials>();
        services.AddSingleton<IEtsyTokenStore>(store);
        services.AddSingleton<IDomainEventPublisher>(events);
        services.AddEtsy(new ConfigurationBuilder().Build());
        services.AddHttpClient(EtsyOAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => http);

        return new Harness(services.BuildServiceProvider(), http, store, events, time);
    }

    private static EtsyTokenSet Tokens(DateTime expires) => new()
    {
        AccessToken = "111.old-access",
        RefreshToken = "111.old-refresh",
        AccessTokenExpiresAtUtc = expires,
        RefreshTokenIssuedAtUtc = expires.AddDays(-1),
        Scopes = "shops_r",
    };

    [Fact]
    public async Task Expired_token_is_refreshed_before_the_call_and_api_key_is_sent()
    {
        var h = Build((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/v3/public/oauth/token" => ScriptedHandler.Json(HttpStatusCode.OK,
                """{"access_token":"111.new-access","token_type":"Bearer","expires_in":3600,"refresh_token":"111.new-refresh"}"""),
            "/v3/application/shops/42" => ScriptedHandler.Json(HttpStatusCode.OK,
                """{"shop_id":42,"shop_name":"TestShop","user_id":111,"currency_code":"USD"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        h.Store.Tokens[1] = Tokens(h.Time.Now.UtcDateTime.AddMinutes(-1));

        var shop = await h.Api.GetShopAsync(42, EtsyAuth.ForShop(1));

        Assert.Equal("TestShop", shop.ShopName);
        Assert.Equal(2, h.Http.Requests.Count);

        var refresh = h.Http.Requests[0];
        Assert.Equal("/v3/public/oauth/token", refresh.RequestUri!.AbsolutePath);
        Assert.False(refresh.Headers.Contains("x-api-key")); // only on /v3/application calls

        var call = h.Http.Requests[1];
        Assert.Equal("Bearer 111.new-access", call.Headers.Authorization?.ToString());
        Assert.Equal("keystring123:secret456", call.Headers.GetValues("x-api-key").Single());
        Assert.Equal("111.new-refresh", h.Store.Tokens[1].RefreshToken);
    }

    [Fact]
    public async Task Valid_token_is_used_without_refresh()
    {
        var h = Build((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"user_id":111,"shop_id":42}"""));
        h.Store.Tokens[1] = Tokens(h.Time.Now.UtcDateTime.AddMinutes(30));

        var me = await h.Api.GetMeAsync(EtsyAuth.ForShop(1));

        Assert.Equal(42L, me.ShopId);
        Assert.Single(h.Http.Requests);
        Assert.Equal("Bearer 111.old-access", h.Http.Requests[0].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Rejected_refresh_token_marks_shop_for_reconnect()
    {
        var h = Build((_, _) => ScriptedHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token expired"}"""));
        h.Store.Tokens[1] = Tokens(h.Time.Now.UtcDateTime.AddMinutes(-1));

        await Assert.ThrowsAsync<EtsyReauthRequiredException>(() => h.Tokens.GetAccessTokenAsync(1));

        Assert.True(h.Store.Tokens[1].NeedsReauth);
        Assert.Contains(h.Events.Events, e => e is ShopNeedsReauth { ShopId: 1 });
    }

    [Fact]
    public async Task Rate_limited_call_becomes_RetryLater()
    {
        var h = Build((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return r;
        });
        h.Store.Tokens[1] = Tokens(h.Time.Now.UtcDateTime.AddMinutes(30));

        var ex = await Assert.ThrowsAsync<EtsyRateLimitedException>(() => h.Api.GetMeAsync(EtsyAuth.ForShop(1)));
        Assert.Equal(TimeSpan.FromMinutes(5), ex.Delay);
    }
}
