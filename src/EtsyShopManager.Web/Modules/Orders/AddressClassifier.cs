using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Infrastructure.Usps;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record AddressClassificationResult(
    string Status,
    string? Alert,
    string? SuggestedAddress,
    string? RawReply,
    bool IsSubstantiveChange);

public static partial class AddressClassifier
{
    private static readonly Dictionary<string, string> SuffixAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["STREET"] = "ST", ["ST"] = "ST",
        ["AVENUE"] = "AVE", ["AVE"] = "AVE", ["AV"] = "AVE",
        ["BOULEVARD"] = "BLVD", ["BLVD"] = "BLVD",
        ["DRIVE"] = "DR", ["DR"] = "DR",
        ["ROAD"] = "RD", ["RD"] = "RD",
        ["LANE"] = "LN", ["LN"] = "LN",
        ["COURT"] = "CT", ["CT"] = "CT",
        ["CIRCLE"] = "CIR", ["CIR"] = "CIR",
        ["PLACE"] = "PL", ["PL"] = "PL",
        ["TERRACE"] = "TER", ["TER"] = "TER",
        ["PARKWAY"] = "PKWY", ["PKWY"] = "PKWY",
        ["HIGHWAY"] = "HWY", ["HWY"] = "HWY",
        ["WAY"] = "WAY",
        ["TRAIL"] = "TRL", ["TRL"] = "TRL",
        ["CROSSING"] = "XING", ["XING"] = "XING",
        ["NORTH"] = "N", ["N"] = "N",
        ["SOUTH"] = "S", ["S"] = "S",
        ["EAST"] = "E", ["E"] = "E",
        ["WEST"] = "W", ["W"] = "W",
        ["NORTHWEST"] = "NW", ["NW"] = "NW",
        ["NORTHEAST"] = "NE", ["NE"] = "NE",
        ["SOUTHWEST"] = "SW", ["SW"] = "SW",
        ["SOUTHEAST"] = "SE", ["SE"] = "SE",
        ["APARTMENT"] = "APT", ["APT"] = "APT",
        ["SUITE"] = "STE", ["STE"] = "STE",
        ["UNIT"] = "UNIT",
        ["BUILDING"] = "BLDG", ["BLDG"] = "BLDG",
        ["FLOOR"] = "FL", ["FL"] = "FL",
        ["ROOM"] = "RM", ["RM"] = "RM",
        ["DEPARTMENT"] = "DEPT", ["DEPT"] = "DEPT"
    };

    [GeneratedRegex(@"[^\w\s]", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    public static AddressClassificationResult Classify(ParsedAddress input, UspsAddressCheckResult uspsResult)
    {
        if (!input.IsUs)
        {
            return new AddressClassificationResult(
                Status: AddressCheckStatuses.SkippedNonUs,
                Alert: null,
                SuggestedAddress: null,
                RawReply: null,
                IsSubstantiveChange: false);
        }

        if (!uspsResult.Success)
        {
            if (uspsResult.StatusCode == 404
                || (uspsResult.ErrorMessage is not null && uspsResult.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return new AddressClassificationResult(
                    Status: AddressCheckStatuses.AddressNotFound,
                    Alert: "Address not found by USPS",
                    SuggestedAddress: null,
                    RawReply: uspsResult.RawResponse,
                    IsSubstantiveChange: true);
            }

            return new AddressClassificationResult(
                Status: AddressCheckStatuses.Error,
                Alert: uspsResult.ErrorMessage ?? "USPS verification error",
                SuggestedAddress: null,
                RawReply: uspsResult.RawResponse,
                IsSubstantiveChange: false);
        }

        var resp = uspsResult.Response;
        if (resp is null)
        {
            return new AddressClassificationResult(
                Status: AddressCheckStatuses.Error,
                Alert: "No address details in USPS response",
                SuggestedAddress: null,
                RawReply: uspsResult.RawResponse,
                IsSubstantiveChange: false);
        }

        var dpv = resp.EffectiveDPVConfirmation?.Trim().ToUpperInvariant();
        var suggested = FormatSuggestedAddress(input.RecipientName, resp);

        switch (dpv)
        {
            case "N":
                return new AddressClassificationResult(
                    Status: AddressCheckStatuses.AddressNotFound,
                    Alert: "Address not found by USPS",
                    SuggestedAddress: null,
                    RawReply: uspsResult.RawResponse,
                    IsSubstantiveChange: true);

            case "D":
                return new AddressClassificationResult(
                    Status: AddressCheckStatuses.MissingUnit,
                    Alert: "Apartment or unit missing",
                    SuggestedAddress: suggested,
                    RawReply: uspsResult.RawResponse,
                    IsSubstantiveChange: true);

            case "S":
                return new AddressClassificationResult(
                    Status: AddressCheckStatuses.UnitNotFound,
                    Alert: "Unit not found by USPS",
                    SuggestedAddress: suggested,
                    RawReply: uspsResult.RawResponse,
                    IsSubstantiveChange: true);

            case "Y":
            default:
                var isSubstantive = IsSubstantiveChange(input, resp);
                if (isSubstantive)
                {
                    return new AddressClassificationResult(
                        Status: AddressCheckStatuses.SuggestedAddress,
                        Alert: "USPS suggests a change to this address",
                        SuggestedAddress: suggested,
                        RawReply: uspsResult.RawResponse,
                        IsSubstantiveChange: true);
                }

                return new AddressClassificationResult(
                    Status: AddressCheckStatuses.Ok,
                    Alert: null,
                    SuggestedAddress: suggested,
                    RawReply: uspsResult.RawResponse,
                    IsSubstantiveChange: false);
        }
    }

    public static bool IsSubstantiveChange(ParsedAddress input, UspsAddressResponse usps)
    {
        // 1. 5-digit ZIP change
        var inZip5 = UsAddressParser.GetZip5(input.Zip);
        var uspsZip5 = UsAddressParser.GetZip5(usps.EffectiveZIPCode);
        if (inZip5.Length == 5 && uspsZip5.Length == 5 && inZip5 != uspsZip5)
            return true;

        // 2. City change
        var inCity = NormalizeCity(input.City);
        var uspsCity = NormalizeCity(usps.EffectiveCity);
        if (!string.IsNullOrEmpty(inCity) && !string.IsNullOrEmpty(uspsCity) && inCity != uspsCity)
            return true;

        // 3. Street line comparison
        var inStreetFull = $"{input.Street1} {input.Street2}".Trim();
        var uspsStreetFull = $"{usps.EffectiveStreetAddress} {usps.EffectiveSecondaryAddress}".Trim();

        var canonInput = CanonicalizeStreet(inStreetFull);
        var canonUsps = CanonicalizeStreet(uspsStreetFull);

        if (canonInput != canonUsps)
            return true;

        return false;
    }

    public static string FormatSuggestedAddress(string? recipientName, UspsAddressResponse resp)
    {
        var street = resp.EffectiveStreetAddress ?? "";
        var sec = resp.EffectiveSecondaryAddress;
        var city = resp.EffectiveCity ?? "";
        var state = resp.EffectiveState ?? "";
        var zip = resp.EffectiveZIPCode ?? "";
        var zip4 = resp.EffectiveZIPPlus4;

        var fullZip = !string.IsNullOrEmpty(zip4) ? $"{zip}-{zip4}" : zip;
        var cityStateZip = $"{city}, {state} {fullZip}".Trim();

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(recipientName))
            lines.Add(recipientName.Trim());

        if (!string.IsNullOrWhiteSpace(street))
            lines.Add(street.Trim());

        if (!string.IsNullOrWhiteSpace(sec))
            lines.Add(sec.Trim());

        if (!string.IsNullOrWhiteSpace(cityStateZip))
            lines.Add(cityStateZip);

        return string.Join("\n", lines);
    }

    private static string NormalizeCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
            return "";

        var cleaned = PunctuationRegex().Replace(city, " ").Trim().ToUpperInvariant();
        var words = WhitespaceRegex().Split(cleaned);
        var canonical = words.Select(w => w switch
        {
            "ST" => "SAINT",
            "MT" => "MOUNT",
            "FT" => "FORT",
            _ => w
        });
        return string.Join(" ", canonical);
    }

    public static string CanonicalizeStreet(string street)
    {
        if (string.IsNullOrWhiteSpace(street))
            return "";

        // Remove punctuation and clean whitespace
        var cleaned = PunctuationRegex().Replace(street, " ").Trim().ToUpperInvariant();
        var tokens = WhitespaceRegex().Split(cleaned);

        var canonical = tokens
            .Where(t => t.Length > 0 && t != "#")
            .Select(t => SuffixAbbreviations.TryGetValue(t, out var abbr) ? abbr : t);

        return string.Join(" ", canonical);
    }
}
