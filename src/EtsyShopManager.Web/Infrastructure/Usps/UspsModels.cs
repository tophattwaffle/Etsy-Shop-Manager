using System.Text.Json.Serialization;

namespace EtsyShopManager.Web.Infrastructure.Usps;

public sealed record UspsTokenRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("grant_type")] string GrantType = "client_credentials");

public sealed class UspsTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; } = 28800;

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed record UspsAddressRequest(
    [property: JsonPropertyName("streetAddress")] string StreetAddress,
    [property: JsonPropertyName("secondaryAddress")] string? SecondaryAddress,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("ZIPCode")] string? ZipCode);

public sealed class UspsAddressFields
{
    [JsonPropertyName("streetAddress")]
    public string? StreetAddress { get; set; }

    [JsonPropertyName("streetAddressAbbreviation")]
    public string? StreetAddressAbbreviation { get; set; }

    [JsonPropertyName("secondaryAddress")]
    public string? SecondaryAddress { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("cityAbbreviation")]
    public string? CityAbbreviation { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("ZIPCode")]
    public string? ZIPCode { get; set; }

    [JsonPropertyName("ZIPPlus4")]
    public string? ZIPPlus4 { get; set; }

    [JsonPropertyName("urbanization")]
    public string? Urbanization { get; set; }
}

public sealed class UspsAdditionalInfo
{
    [JsonPropertyName("DPVConfirmation")]
    public string? DPVConfirmation { get; set; }

    [JsonPropertyName("DPVCMRA")]
    public string? DPVCMRA { get; set; }

    [JsonPropertyName("DPVFootnotes")]
    public string? DPVFootnotes { get; set; }

    [JsonPropertyName("business")]
    public string? Business { get; set; }
}

public sealed class UspsAddressResponse
{
    // Nested structure
    [JsonPropertyName("address")]
    public UspsAddressFields? Address { get; set; }

    [JsonPropertyName("additionalInfo")]
    public UspsAdditionalInfo? AdditionalInfo { get; set; }

    // Flat structure (supported directly on root)
    [JsonPropertyName("streetAddress")]
    public string? StreetAddress { get; set; }

    [JsonPropertyName("streetAddressAbbreviation")]
    public string? StreetAddressAbbreviation { get; set; }

    [JsonPropertyName("secondaryAddress")]
    public string? SecondaryAddress { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("cityAbbreviation")]
    public string? CityAbbreviation { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("ZIPCode")]
    public string? ZIPCode { get; set; }

    [JsonPropertyName("ZIPPlus4")]
    public string? ZIPPlus4 { get; set; }

    [JsonPropertyName("DPVConfirmation")]
    public string? DPVConfirmation { get; set; }

    [JsonPropertyName("DPVFootnotes")]
    public string? DPVFootnotes { get; set; }

    [JsonIgnore]
    public string? EffectiveStreetAddress => Address?.StreetAddress ?? StreetAddress;

    [JsonIgnore]
    public string? EffectiveSecondaryAddress => Address?.SecondaryAddress ?? SecondaryAddress;

    [JsonIgnore]
    public string? EffectiveCity => Address?.City ?? City;

    [JsonIgnore]
    public string? EffectiveState => Address?.State ?? State;

    [JsonIgnore]
    public string? EffectiveZIPCode => Address?.ZIPCode ?? ZIPCode;

    [JsonIgnore]
    public string? EffectiveZIPPlus4 => Address?.ZIPPlus4 ?? ZIPPlus4;

    [JsonIgnore]
    public string? EffectiveDPVConfirmation => AdditionalInfo?.DPVConfirmation ?? DPVConfirmation;

    [JsonIgnore]
    public string? EffectiveDPVFootnotes => AdditionalInfo?.DPVFootnotes ?? DPVFootnotes;
}

public sealed class UspsErrorResponse
{
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; set; }

    [JsonPropertyName("error")]
    public UspsErrorBody? Error { get; set; }
}

public sealed class UspsErrorBody
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errors")]
    public List<UspsErrorDetail>? Errors { get; set; }
}

public sealed class UspsErrorDetail
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public sealed record UspsAddressCheckResult(
    bool Success,
    int StatusCode,
    UspsAddressResponse? Response,
    string? RawResponse,
    string? ErrorMessage);

public sealed record UspsConnectionTestResult(
    bool Success,
    string Message,
    int? StatusCode = null,
    string? Details = null);
