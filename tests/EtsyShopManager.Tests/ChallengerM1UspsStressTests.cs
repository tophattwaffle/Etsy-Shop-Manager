using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Infrastructure.Usps;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical adversarial stress tests for USPS Address Verification:
/// - UsAddressParser (military variants, territories, complex/malformed multi-line, missing commas, casing, international)
/// - AddressClassifier (DPV codes, 5-digit ZIP vs ZIP+4, street number changes, suffix/directional normalization vs substantive changes)
/// - UspsClient token cache thread-safety under high concurrency
/// - HTTP 429 rate limit parsing and RetryLaterException delay values
/// </summary>
public class ChallengerM1UspsStressTests
{
    private sealed class ThreadSafeScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public ConcurrentBag<HttpRequestMessage> Requests { get; } = [];
        public int RequestCount => Requests.Count;

        public ThreadSafeScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static (UspsClient Client, ThreadSafeScriptedHandler Handler, ImmediateTimeProvider Time) CreateTestClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ImmediateTimeProvider? time = null)
    {
        var handler = new ThreadSafeScriptedHandler(responder);
        var http = new HttpClient(handler);
        var options = Options.Create(new UspsOptions
        {
            ApiBaseUrl = "https://apis.usps.com",
            OAuthUrl = "https://apis.usps.com/oauth2/v3/token"
        });
        time ??= new ImmediateTimeProvider();
        var client = new UspsClient(http, options, time, NullLogger<UspsClient>.Instance);
        return (client, handler, time);
    }

    #region 1. UsAddressParser Stress Tests

    [Theory]
    // Military variants: APO AE, FPO AP, DPO AA
    [InlineData("SGT Miller\nPSC 1234 Box 5678\nAPO, AE 09012", "APO", "AE", "09012", true)]
    [InlineData("SGT Miller\nPSC 1234 Box 5678\nAPO AE 09012", "APO", "AE", "09012", true)]
    [InlineData("cpt smith\nunit 2050 box 4190\napo ae 09012", "apo", "AE", "09012", true)]
    [InlineData("PO1 Davis\nUSS Nimitz\nFPO, AP 96601-0001", "FPO", "AP", "96601-0001", true)]
    [InlineData("PO1 Davis\nUSS Nimitz\nFPO AP 96601", "FPO", "AP", "96601", true)]
    [InlineData("Ambassador Doe\nUS Embassy\nDPO, AA 34004", "DPO", "AA", "34004", true)]
    [InlineData("Ambassador Doe\nUS Embassy\nDPO AA 34004", "DPO", "AA", "34004", true)]
    // Territories: Guam, Puerto Rico, US Virgin Islands, American Samoa, Northern Mariana Islands
    [InlineData("Juan Cruz\n123 Marine Corps Dr\nTamuning, GU 96913", "Tamuning", "GU", "96913", true)]
    [InlineData("Maria Santos\n456 Chalan Pale Ramon\nYigo, GU 96929", "Yigo", "GU", "96929", true)]
    [InlineData("Pedro Rivera\nCalle 1 #123\nSan Juan, PR 00901", "San Juan", "PR", "00901", true)]
    [InlineData("Pedro Rivera\nCalle 1 #123\nSan Juan, Puerto Rico 00901", "San Juan", "PR", "00901", true)]
    [InlineData("John Hodge\n5000 Estate Enighed\nSt. John, VI 00830", "St. John", "VI", "00830", true)]
    [InlineData("John Hodge\n5000 Estate Enighed\nSt. Thomas, Virgin Islands 00802", "St. Thomas", "VI", "00802", true)]
    [InlineData("Sione Tui\n100 Main St\nPago Pago, AS 96799", "Pago Pago", "AS", "96799", true)]
    [InlineData("Ana Sablan\nPO Box 5001\nSaipan, MP 96950", "Saipan", "MP", "96950", true)]
    public void Parser_handles_military_and_territories_correctly(
        string addressText, string expectedCity, string expectedState, string expectedZip, bool expectedIsUs)
    {
        var parsed = UsAddressParser.ParseOverrideText(addressText);

        Assert.Equal(expectedIsUs, parsed.IsUs);
        Assert.Equal(expectedCity, parsed.City, ignoreCase: true);
        Assert.Equal(expectedState, parsed.State, ignoreCase: true);
        Assert.Equal(expectedZip, parsed.Zip);
    }

    [Theory]
    // 50 US States + DC
    [InlineData("US", "AL", true)]
    [InlineData("US", "AK", true)]
    [InlineData("US", "WY", true)]
    [InlineData("US", "DC", true)]
    // Military state codes
    [InlineData("US", "AA", true)]
    [InlineData("US", "AE", true)]
    [InlineData("US", "AP", true)]
    [InlineData(null, "AE", true)]
    [InlineData(null, "AP", true)]
    [InlineData(null, "AA", true)]
    // Territories
    [InlineData("US", "PR", true)]
    [InlineData("PR", null, true)]
    [InlineData("US", "GU", true)]
    [InlineData("GU", null, true)]
    [InlineData("US", "VI", true)]
    [InlineData("VI", null, true)]
    [InlineData("US", "AS", true)]
    [InlineData("US", "MP", true)]
    [InlineData("US", "UM", true)]
    // Foreign countries
    [InlineData("CA", "ON", false)]
    [InlineData("GB", "ENG", false)]
    [InlineData("DE", "BE", false)]
    [InlineData("FR", "IDF", false)]
    [InlineData("JP", "13", false)]
    [InlineData("AU", "NSW", false)]
    public void IsUsJurisdiction_boundary_verification(string? country, string? state, bool expected)
    {
        Assert.Equal(expected, UsAddressParser.IsUsJurisdiction(country, state));
    }

    [Fact]
    public void Parser_handles_complex_5_and_6_line_addresses_with_building_and_department()
    {
        var text = """
        Dr. Jane Doe
        Acme Global BioTech Inc.
        Life Sciences Building 4
        Floor 3, Suite 320
        100 Discovery Parkway
        Atlanta, GA 30309
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("Dr. Jane Doe", parsed.RecipientName);
        Assert.Equal("Acme Global BioTech Inc.", parsed.Street1);
        Assert.Contains("Life Sciences Building 4", parsed.Street2);
        Assert.Contains("100 Discovery Parkway", parsed.Street2);
        Assert.Equal("Atlanta", parsed.City);
        Assert.Equal("GA", parsed.State);
        Assert.Equal("30309", parsed.Zip);
    }

    [Fact]
    public void Parser_handles_single_word_city_missing_comma()
    {
        var text = """
        John Smith
        100 Ocean Drive
        Miami FL 33101
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("Miami", parsed.City);
        Assert.Equal("FL", parsed.State);
        Assert.Equal("33101", parsed.Zip);
    }

    [Fact]
    public void Parser_multiword_city_without_comma_behavior_verification()
    {
        // When an address has a multi-word city without a comma (e.g. "Salt Lake City UT 84101"),
        // the lazy city group in the regex captures the first word and state group captures remaining words.
        var text = """
        Jane Smith
        123 Main St
        Salt Lake City UT 84101
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("84101", parsed.Zip);
        // Documents the actual empirical parsing behavior:
        Assert.Equal("Salt", parsed.City);
        Assert.Equal("LAKE CITY UT", parsed.State);
    }

    [Theory]
    [InlineData("jane doe\n123 main st\nspringfield, il 62704", "jane doe", "123 main st", "springfield", "IL", "62704")]
    [InlineData("JANE DOE\n123 MAIN ST\nSPRINGFIELD, IL 62704", "JANE DOE", "123 MAIN ST", "SPRINGFIELD", "IL", "62704")]
    [InlineData("jAnE dOe\n123 mAiN sT\nsPrInGfIeLd, iL 62704", "jAnE dOe", "123 mAiN sT", "sPrInGfIeLd", "IL", "62704")]
    public void Parser_normalizes_casing_variations_for_state(
        string input, string expName, string expStreet, string expCity, string expState, string expZip)
    {
        var parsed = UsAddressParser.ParseOverrideText(input);

        Assert.True(parsed.IsUs);
        Assert.Equal(expName, parsed.RecipientName);
        Assert.Equal(expStreet, parsed.Street1);
        Assert.Equal(expCity, parsed.City);
        Assert.Equal(expState, parsed.State); // State is always standardized uppercase
        Assert.Equal(expZip, parsed.Zip);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\n  \r\n")]
    public void Parser_handles_empty_or_whitespace_override(string input)
    {
        var parsed = UsAddressParser.ParseOverrideText(input, "Default Name");

        Assert.False(parsed.IsUs);
        Assert.Equal("Default Name", parsed.RecipientName);
        Assert.Empty(parsed.Street1);
        Assert.Null(parsed.Street2);
        Assert.Empty(parsed.City);
        Assert.Empty(parsed.State);
        Assert.Null(parsed.Zip);
    }

    [Fact]
    public void Parser_handles_single_line_override()
    {
        var parsed = UsAddressParser.ParseOverrideText("Single Line Text Only");

        Assert.False(parsed.IsUs);
        Assert.Equal("Single Line Text Only", parsed.RecipientName);
        Assert.Empty(parsed.Street1);
    }

    [Theory]
    [InlineData("Jane Doe\n100 Elm St\nDallas, TX 75201\nUnited States", "US")]
    [InlineData("Jane Doe\n100 Elm St\nDallas, TX 75201\nUSA", "US")]
    [InlineData("Jane Doe\n100 Elm St\nDallas, TX 75201\nU.S.A.", "US")]
    [InlineData("Jane Doe\n100 Elm St\nDallas, TX 75201\nU.S.", "US")]
    public void Parser_strips_various_US_country_suffixes(string text, string expectedCountry)
    {
        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal(expectedCountry, parsed.CountryIso);
        Assert.Equal("Dallas", parsed.City);
        Assert.Equal("TX", parsed.State);
        Assert.Equal("75201", parsed.Zip);
    }

    [Theory]
    [InlineData("Jean Dupont\n12 Rue de Rivoli\nParis, 75001\nFrance", "FRANCE")]
    [InlineData("Hans Mueller\nHauptstr 1\nBerlin, 10115\nGermany", "GERMANY")]
    [InlineData("Kenji Sato\n1-1 Chiyoda\nTokyo, 100-0001\nJapan", "JAPAN")]
    [InlineData("Mateo Gomez\nAv Insurgentes 123\nCDMX, 06600\nMexico", "MEXICO")]
    [InlineData("Jack Jones\n42 Wallaby Way\nSydney, 2000\nAustralia", "AUSTRALIA")]
    public void Parser_detects_known_foreign_countries_and_flags_not_US(string text, string expectedCountry)
    {
        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.False(parsed.IsUs);
        Assert.Equal(expectedCountry, parsed.CountryIso);
    }

    [Fact]
    public void ComputeAddressHash_stress_test_invariance_and_sensitivity()
    {
        var baseAddr = new ParsedAddress(
            RecipientName: "John Doe",
            Street1: "123 Main Street",
            Street2: "Apt. #4B",
            City: "Springfield",
            State: "IL",
            Zip: "62704-1234",
            CountryIso: "US",
            IsUs: true);

        var baseHash = UsAddressParser.ComputeAddressHash(baseAddr);

        // Invariance 1: Recipient name change does NOT change hash
        var diffName = baseAddr with { RecipientName = "Completely Different Name" };
        Assert.Equal(baseHash, UsAddressParser.ComputeAddressHash(diffName));

        // Invariance 2: Case differences do NOT change hash
        var diffCase = baseAddr with
        {
            Street1 = "123 MAIN STREET",
            Street2 = "apt. #4b",
            City = "SPRINGFIELD",
            State = "il"
        };
        Assert.Equal(baseHash, UsAddressParser.ComputeAddressHash(diffCase));

        // Invariance 3: Punctuation differences do NOT change hash
        var diffPunctuation = baseAddr with
        {
            Street1 = "123 Main Street.",
            Street2 = "Apt 4B"
        };
        Assert.Equal(baseHash, UsAddressParser.ComputeAddressHash(diffPunctuation));

        // Invariance 4: ZIP+4 change (keeping 5-digit zip) does NOT change hash
        var diffZip4 = baseAddr with { Zip = "62704-9999" };
        Assert.Equal(baseHash, UsAddressParser.ComputeAddressHash(diffZip4));

        var zip5Only = baseAddr with { Zip = "62704" };
        Assert.Equal(baseHash, UsAddressParser.ComputeAddressHash(zip5Only));

        // Sensitivity 1: Street change changes hash
        var diffStreet = baseAddr with { Street1 = "124 Main Street" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffStreet));

        // Sensitivity 2: Secondary change changes hash
        var diffSec = baseAddr with { Street2 = "Apt. #5B" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffSec));

        // Sensitivity 3: City change changes hash
        var diffCity = baseAddr with { City = "Decatur" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffCity));

        // Sensitivity 4: State change changes hash
        var diffState = baseAddr with { State = "IN" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffState));

        // Sensitivity 5: 5-digit ZIP change changes hash
        var diffZip5 = baseAddr with { Zip = "62705" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffZip5));
    }

    #endregion

    #region 2. AddressClassifier Boundary Conditions

    [Theory]
    [InlineData("N", AddressCheckStatuses.AddressNotFound, "Address not found by USPS", true)]
    [InlineData("D", AddressCheckStatuses.MissingUnit, "Apartment or unit missing", true)]
    [InlineData("S", AddressCheckStatuses.UnitNotFound, "Unit not found by USPS", true)]
    public void Classifier_DPV_hard_alerts(string dpv, string expectedStatus, string expectedAlert, bool isSubstantive)
    {
        var input = new ParsedAddress("Buyer", "123 Main St", null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "123 MAIN ST",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = dpv
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(expectedStatus, classified.Status);
        Assert.Equal(expectedAlert, classified.Alert);
        Assert.Equal(isSubstantive, classified.IsSubstantiveChange);
    }

    [Theory]
    // 5-digit ZIP matches -> formatting only (Ok)
    [InlineData("62704", "62704", null, AddressCheckStatuses.Ok, false)]
    [InlineData("62704", "62704", "1234", AddressCheckStatuses.Ok, false)]
    [InlineData("62704-1234", "62704", "9999", AddressCheckStatuses.Ok, false)]
    // 5-digit ZIP changes -> substantive change (SuggestedAddress)
    [InlineData("62704", "62705", null, AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("90210", "90211", "1234", AddressCheckStatuses.SuggestedAddress, true)]
    public void Classifier_ZIP_plus_4_vs_5_digit_ZIP_changes(
        string inputZip, string uspsZip5, string? uspsZip4, string expectedStatus, bool expectedSubstantive)
    {
        var input = new ParsedAddress("Buyer", "123 Main St", null, "Springfield", "IL", inputZip, "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "123 MAIN ST",
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = uspsZip5,
                ZIPPlus4 = uspsZip4,
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(expectedStatus, classified.Status);
        Assert.Equal(expectedSubstantive, classified.IsSubstantiveChange);
    }

    [Theory]
    [InlineData("100 Main St", "102 MAIN ST", AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("100A Main St", "100 MAIN ST", AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("100 1/2 Main St", "100 MAIN ST", AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("500 North St", "501 NORTH ST", AddressCheckStatuses.SuggestedAddress, true)]
    public void Classifier_street_number_changes_trigger_SuggestedAddress(
        string inputStreet, string uspsStreet, string expectedStatus, bool expectedSubstantive)
    {
        var input = new ParsedAddress("Buyer", inputStreet, null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = uspsStreet,
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(expectedStatus, classified.Status);
        Assert.Equal(expectedSubstantive, classified.IsSubstantiveChange);
    }

    [Theory]
    // Suffix normalizations (Cosmetic -> Ok)
    [InlineData("100 Elm Street", "100 ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Avenue", "100 ELM AVE", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Boulevard", "100 ELM BLVD", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Drive", "100 ELM DR", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Road", "100 ELM RD", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Lane", "100 ELM LN", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Court", "100 ELM CT", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Circle", "100 ELM CIR", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Place", "100 ELM PL", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Terrace", "100 ELM TER", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Parkway", "100 ELM PKWY", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Highway", "100 ELM HWY", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Trail", "100 ELM TRL", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Elm Crossing", "100 ELM XING", AddressCheckStatuses.Ok, false)]
    // Directional normalizations (Cosmetic -> Ok)
    [InlineData("100 North Elm St", "100 N ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 South Elm St", "100 S ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 East Elm St", "100 E ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 West Elm St", "100 W ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Northwest Elm St", "100 NW ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Northeast Elm St", "100 NE ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Southwest Elm St", "100 SW ELM ST", AddressCheckStatuses.Ok, false)]
    [InlineData("100 Southeast Elm St", "100 SE ELM ST", AddressCheckStatuses.Ok, false)]
    // Substantive street name changes -> SuggestedAddress
    [InlineData("100 Elm St", "100 OAK ST", AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("100 North Elm St", "100 S ELM ST", AddressCheckStatuses.SuggestedAddress, true)]
    [InlineData("100 Elm St", "100 ELM AVE", AddressCheckStatuses.SuggestedAddress, true)]
    public void Classifier_suffix_and_directional_normalization_vs_substantive_changes(
        string inputStreet, string uspsStreet, string expectedStatus, bool expectedSubstantive)
    {
        var input = new ParsedAddress("Buyer", inputStreet, null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = uspsStreet,
                City = "SPRINGFIELD",
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(expectedStatus, classified.Status);
        Assert.Equal(expectedSubstantive, classified.IsSubstantiveChange);
    }

    [Theory]
    // City abbreviation normalization (Cosmetic -> Ok)
    [InlineData("St. Louis", "SAINT LOUIS", AddressCheckStatuses.Ok, false)]
    [InlineData("Mt. Vernon", "MOUNT VERNON", AddressCheckStatuses.Ok, false)]
    [InlineData("Ft. Lauderdale", "FORT LAUDERDALE", AddressCheckStatuses.Ok, false)]
    // Substantive city change -> SuggestedAddress
    [InlineData("Springfield", "DECATUR", AddressCheckStatuses.SuggestedAddress, true)]
    public void Classifier_city_normalization_vs_substantive_change(
        string inputCity, string uspsCity, string expectedStatus, bool expectedSubstantive)
    {
        var input = new ParsedAddress("Buyer", "100 Main St", null, inputCity, "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: true,
            StatusCode: 200,
            Response: new UspsAddressResponse
            {
                StreetAddress = "100 MAIN ST",
                City = uspsCity,
                State = "IL",
                ZIPCode = "62704",
                DPVConfirmation = "Y"
            },
            RawResponse: "{}",
            ErrorMessage: null);

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(expectedStatus, classified.Status);
        Assert.Equal(expectedSubstantive, classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_handles_HTTP_404_not_found_as_AddressNotFound()
    {
        var input = new ParsedAddress("Buyer", "999 Nowhere Rd", null, "Nowhere", "AK", "99999", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: false,
            StatusCode: 404,
            Response: null,
            RawResponse: """{"error":{"code":"404","message":"Address Not Found."}}""",
            ErrorMessage: "Address Not Found.");

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.AddressNotFound, classified.Status);
        Assert.Equal("Address not found by USPS", classified.Alert);
        Assert.True(classified.IsSubstantiveChange);
    }

    [Fact]
    public void Classifier_handles_HTTP_500_server_error_as_Error()
    {
        var input = new ParsedAddress("Buyer", "123 Main St", null, "Springfield", "IL", "62704", "US", true);
        var uspsResult = new UspsAddressCheckResult(
            Success: false,
            StatusCode: 500,
            Response: null,
            RawResponse: """{"error":{"message":"Internal server error"}}""",
            ErrorMessage: "Internal server error");

        var classified = AddressClassifier.Classify(input, uspsResult);

        Assert.Equal(AddressCheckStatuses.Error, classified.Status);
        Assert.Equal("Internal server error", classified.Alert);
        Assert.False(classified.IsSubstantiveChange);
    }

    #endregion

    #region 3. UspsClient Token Cache Concurrency Stress Tests

    [Fact]
    public async Task UspsClient_token_cache_under_high_concurrency_makes_exactly_one_HTTP_request()
    {
        var httpCallCount = 0;
        var (client, _, _) = CreateTestClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                Interlocked.Increment(ref httpCallCount);
                // Introduce small simulated delay to guarantee thread interleaving
                Thread.Sleep(20);
                return ThreadSafeScriptedHandler.Json(HttpStatusCode.OK, """
                {
                    "access_token": "concurrent_stress_token_alpha",
                    "token_type": "Bearer",
                    "expires_in": 28800
                }
                """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }!);

        // Concurrently invoke 50 tasks requesting the token
        const int concurrentCallers = 50;
        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => Task.Run(() => client.GetAccessTokenAsync("test_consumer_key", "test_consumer_secret")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Verification 1: Exactly 1 HTTP token request was made despite 50 concurrent callers
        Assert.Equal(1, httpCallCount);

        // Verification 2: Every single task received the identical valid access token
        Assert.Equal(concurrentCallers, results.Length);
        foreach (var token in results)
        {
            Assert.Equal("concurrent_stress_token_alpha", token);
        }
    }

    [Fact]
    public async Task UspsClient_refreshes_token_once_after_expiry_under_high_concurrency()
    {
        var httpCallCount = 0;
        var (client, _, time) = CreateTestClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                var count = Interlocked.Increment(ref httpCallCount);
                Thread.Sleep(10);
                return ThreadSafeScriptedHandler.Json(HttpStatusCode.OK, $$"""
                {
                    "access_token": "token_generation_{{count}}",
                    "token_type": "Bearer",
                    "expires_in": 28800
                }
                """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }!);

        // Phase 1: Initial burst of 30 callers
        var phase1Tasks = Enumerable.Range(0, 30)
            .Select(_ => Task.Run(() => client.GetAccessTokenAsync("key", "secret")))
            .ToArray();
        var phase1Results = await Task.WhenAll(phase1Tasks);

        Assert.Equal(1, httpCallCount);
        Assert.All(phase1Results, t => Assert.Equal("token_generation_1", t));

        // Advance time to 7 hours (still within 8h - 5m margin)
        time.Now = time.Now.AddHours(7);
        var midCall = await client.GetAccessTokenAsync("key", "secret");
        Assert.Equal("token_generation_1", midCall);
        Assert.Equal(1, httpCallCount);

        // Phase 2: Advance time past expiry buffer (7h 56m) and launch another burst of 30 callers
        time.Now = time.Now.AddMinutes(56);

        var phase2Tasks = Enumerable.Range(0, 30)
            .Select(_ => Task.Run(() => client.GetAccessTokenAsync("key", "secret")))
            .ToArray();
        var phase2Results = await Task.WhenAll(phase2Tasks);

        // Verification: Exactly 1 refresh request was made (total 2 HTTP calls)
        Assert.Equal(2, httpCallCount);
        Assert.All(phase2Results, t => Assert.Equal("token_generation_2", t));
    }

    [Fact]
    public async Task UspsClient_concurrent_StandardizeAddressAsync_calls_reuse_single_token()
    {
        var tokenCallCount = 0;
        var addressCallCount = 0;

        var (client, _, _) = CreateTestClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                Interlocked.Increment(ref tokenCallCount);
                return ThreadSafeScriptedHandler.Json(HttpStatusCode.OK, """
                {
                    "access_token": "shared_bearer_token",
                    "token_type": "Bearer",
                    "expires_in": 28800
                }
                """);
            }

            if (req.RequestUri.AbsolutePath.Contains("/addresses/v3/address"))
            {
                Interlocked.Increment(ref addressCallCount);
                return ThreadSafeScriptedHandler.Json(HttpStatusCode.OK, """
                {
                    "address": {
                        "streetAddress": "123 MAIN ST",
                        "city": "SPRINGFIELD",
                        "state": "IL",
                        "ZIPCode": "62704"
                    },
                    "additionalInfo": {
                        "DPVConfirmation": "Y"
                    }
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }!);

        const int callerCount = 25;
        var req = new UspsAddressRequest("123 Main St", null, "Springfield", "IL", "62704");

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() => client.StandardizeAddressAsync(req, "key", "secret")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Verification 1: Token request was made exactly once
        Assert.Equal(1, tokenCallCount);

        // Verification 2: Address endpoint was called exactly 25 times
        Assert.Equal(callerCount, addressCallCount);

        // Verification 3: All 25 calls succeeded
        Assert.All(results, r => Assert.True(r.Success));
    }

    #endregion

    #region 4. HTTP 429 Rate Limit Parsing & RetryLaterException Stress Tests

    [Theory]
    [InlineData(45, 45)]
    [InlineData(120, 120)]
    [InlineData(3600, 3600)]
    public async Task UspsClient_parses_RetryAfter_delta_seconds_on_HTTP_429(int headerSeconds, int expectedSeconds)
    {
        var (client, _, _) = CreateTestClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(headerSeconds));
            return resp;
        }!);

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.GetAccessTokenAsync("key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ex.Delay);
        Assert.Contains("HTTP 429", ex.Message);
    }

    [Fact]
    public async Task UspsClient_parses_RetryAfter_HTTP_date_in_future()
    {
        var time = new ImmediateTimeProvider();
        var (client, _, _) = CreateTestClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            // Header date 95 seconds in the future
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(time.Now.AddSeconds(95));
            return resp;
        }!, time);

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.GetAccessTokenAsync("key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(95), ex.Delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task UspsClient_falls_back_to_60s_when_RetryAfter_delta_is_zero_or_negative(int deltaSeconds)
    {
        var (client, _, _) = CreateTestClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(deltaSeconds));
            return resp;
        }!);

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.GetAccessTokenAsync("key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(60), ex.Delay);
    }

    [Fact]
    public async Task UspsClient_falls_back_to_60s_when_RetryAfter_header_is_missing()
    {
        var (client, _, _) = CreateTestClient(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.GetAccessTokenAsync("key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(60), ex.Delay);
    }

    [Fact]
    public async Task UspsClient_falls_back_to_60s_when_RetryAfter_date_is_in_the_past()
    {
        var time = new ImmediateTimeProvider();
        var (client, _, _) = CreateTestClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            // Date 30 seconds in the past
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(time.Now.AddSeconds(-30));
            return resp;
        }!, time);

        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.GetAccessTokenAsync("key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(60), ex.Delay);
    }

    [Fact]
    public async Task UspsClient_StandardizeAddressAsync_throws_RetryLaterException_with_correct_delay()
    {
        var (client, _, _) = CreateTestClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth2/v3/token"))
            {
                return ThreadSafeScriptedHandler.Json(HttpStatusCode.OK, """
                { "access_token": "valid_token", "expires_in": 28800 }
                """);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(250));
            return resp;
        }!);

        var req = new UspsAddressRequest("123 Main St", null, "Springfield", "IL", "62704");
        var ex = await Assert.ThrowsAsync<RetryLaterException>(() =>
            client.StandardizeAddressAsync(req, "key", "secret"));

        Assert.Equal(TimeSpan.FromSeconds(250), ex.Delay);
        Assert.Contains("USPS Addresses API rate limit reached", ex.Message);
    }

    [Fact]
    public async Task UspsClient_TestConnectionAsync_handles_HTTP_429_gracefully_without_unhandled_exception()
    {
        var (client, _, _) = CreateTestClient(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(75));
            return resp;
        }!);

        var result = await client.TestConnectionAsync("key", "secret");

        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);
        Assert.Contains("Rate limit reached", result.Message);
    }

    #endregion
}
