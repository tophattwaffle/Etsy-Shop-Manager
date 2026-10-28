using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record ParsedAddress(
    string? RecipientName,
    string Street1,
    string? Street2,
    string City,
    string State,
    string? Zip,
    string CountryIso,
    bool IsUs)
{
    public string FormattedMultiline =>
        string.Join("\n", new[]
        {
            RecipientName,
            Street1,
            Street2,
            $"{City}, {State} {Zip}".Trim(),
            CountryIso != "US" && !string.IsNullOrEmpty(CountryIso) ? CountryIso : null
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static partial class UsAddressParser
{
    // 50 US States + DC
    private static readonly HashSet<string> UsStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
        "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
        "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
        "DC"
    };

    // Inhabited US Territories
    private static readonly HashSet<string> UsTerritoryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PR", "AS", "GU", "MP", "VI", "UM"
    };

    // Military / Diplomatic Armed Forces state codes
    private static readonly HashSet<string> MilitaryStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AA", "AE", "AP"
    };

    private static readonly Dictionary<string, string> StateNamesToCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL", ["Alaska"] = "AK", ["Arizona"] = "AZ", ["Arkansas"] = "AR", ["California"] = "CA",
        ["Colorado"] = "CO", ["Connecticut"] = "CT", ["Delaware"] = "DE", ["Florida"] = "FL", ["Georgia"] = "GA",
        ["Hawaii"] = "HI", ["Idaho"] = "ID", ["Illinois"] = "IL", ["Indiana"] = "IN", ["Iowa"] = "IA",
        ["Kansas"] = "KS", ["Kentucky"] = "KY", ["Louisiana"] = "LA", ["Maine"] = "ME", ["Maryland"] = "MD",
        ["Massachusetts"] = "MA", ["Michigan"] = "MI", ["Minnesota"] = "MN", ["Mississippi"] = "MS", ["Missouri"] = "MO",
        ["Montana"] = "MT", ["Nebraska"] = "NE", ["Nevada"] = "NV", ["New Hampshire"] = "NH", ["New Jersey"] = "NJ",
        ["New Mexico"] = "NM", ["New York"] = "NY", ["North Carolina"] = "NC", ["North Dakota"] = "ND", ["Ohio"] = "OH",
        ["Oklahoma"] = "OK", ["Oregon"] = "OR", ["Pennsylvania"] = "PA", ["Rhode Island"] = "RI", ["South Carolina"] = "SC",
        ["South Dakota"] = "SD", ["Tennessee"] = "TN", ["Texas"] = "TX", ["Utah"] = "UT", ["Vermont"] = "VT",
        ["Virginia"] = "VA", ["Washington"] = "WA", ["West Virginia"] = "WV", ["Wisconsin"] = "WI", ["Wyoming"] = "WY",
        ["District of Columbia"] = "DC", ["Puerto Rico"] = "PR", ["Guam"] = "GU", ["Virgin Islands"] = "VI",
        ["American Samoa"] = "AS", ["Northern Mariana Islands"] = "MP"
    };

    private static readonly HashSet<string> UsCountryIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "USA", "UNITED STATES", "UNITED STATES OF AMERICA", "U.S.", "U.S.A."
    };

    [GeneratedRegex(@"^(?<city>[A-Za-z\s.'-]+?)[,\s]+(?<state>[A-Za-z]{2}|[A-Za-z\s]+?)\s+(?<zip>\d{5}(?:-\d{4})?)$", RegexOptions.Compiled)]
    private static partial Regex CityStateZipRegex();

    [GeneratedRegex(@"[^\w\s]", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    public static bool IsUsJurisdiction(string? countryIso, string? state)
    {
        var cleanCountry = countryIso?.Trim().ToUpperInvariant() ?? "";
        var cleanState = state?.Trim().ToUpperInvariant() ?? "";

        if (cleanCountry == "US" || UsCountryIdentifiers.Contains(cleanCountry) || UsTerritoryCodes.Contains(cleanCountry))
            return true;

        if (UsStateCodes.Contains(cleanState) || UsTerritoryCodes.Contains(cleanState) || MilitaryStateCodes.Contains(cleanState))
            return true;

        if (cleanCountry.Length == 0 && StateNamesToCodes.ContainsKey(cleanState))
            return true;

        return false;
    }

    public static ParsedAddress Parse(Order order)
    {
        if (!string.IsNullOrWhiteSpace(order.AddressOverride))
            return ParseOverrideText(order.AddressOverride, order.BuyerName);

        return ParseFromReceipt(order);
    }

    public static ParsedAddress ParseOverrideText(string overrideText, string? defaultRecipientName = null)
    {
        var lines = (overrideText ?? "").Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (lines.Count == 0)
        {
            return new ParsedAddress(
                RecipientName: defaultRecipientName,
                Street1: "",
                Street2: null,
                City: "",
                State: "",
                Zip: null,
                CountryIso: "US",
                IsUs: false);
        }

        var recipientName = lines[0];
        var remaining = lines.Skip(1).ToList();

        if (remaining.Count == 0)
        {
            return new ParsedAddress(
                RecipientName: recipientName,
                Street1: "",
                Street2: null,
                City: "",
                State: "",
                Zip: null,
                CountryIso: "US",
                IsUs: false);
        }

        // Check if the last line is a country designator
        var country = "US";
        var isUs = true;
        if (remaining.Count > 1 && UsCountryIdentifiers.Contains(remaining[^1]))
        {
            remaining.RemoveAt(remaining.Count - 1);
        }
        else if (remaining.Count > 1 && IsKnownForeignCountry(remaining[^1]))
        {
            country = remaining[^1].ToUpperInvariant();
            isUs = false;
            remaining.RemoveAt(remaining.Count - 1);
        }

        // Parse City, State, ZIP from the last remaining line
        string city = "";
        string state = "";
        string? zip = null;

        var lastLine = remaining[^1];
        var match = CityStateZipRegex().Match(lastLine);
        if (match.Success)
        {
            city = match.Groups["city"].Value.Trim();
            var rawState = match.Groups["state"].Value.Trim();
            state = NormalizeState(rawState);
            zip = match.Groups["zip"].Value.Trim();
            remaining.RemoveAt(remaining.Count - 1);
        }
        else
        {
            // Try comma-separated fallback: "City, State ZIP"
            var parts = lastLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                city = parts[0];
                var stateZip = parts[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (stateZip.Length >= 2)
                {
                    state = NormalizeState(stateZip[0]);
                    zip = stateZip[1];
                    remaining.RemoveAt(remaining.Count - 1);
                }
            }
        }

        // Remaining lines are street addresses
        string street1 = "";
        string? street2 = null;

        if (remaining.Count == 1)
        {
            street1 = remaining[0];
        }
        else if (remaining.Count == 2)
        {
            street1 = remaining[0];
            street2 = remaining[1];
        }
        else if (remaining.Count > 2)
        {
            street1 = remaining[0];
            street2 = string.Join(", ", remaining.Skip(1));
        }

        if (!IsUsJurisdiction(country, state))
            isUs = false;

        return new ParsedAddress(
            RecipientName: recipientName,
            Street1: street1,
            Street2: street2,
            City: city,
            State: state,
            Zip: zip,
            CountryIso: isUs ? "US" : country,
            IsUs: isUs);
    }

    public static ParsedAddress ParseFromReceipt(Order order)
    {
        JsonObject? receipt = null;
        try { receipt = JsonNode.Parse(order.RawJson) as JsonObject; }
        catch { }

        string? S(string key) => receipt?[key] is JsonValue v && v.TryGetValue<string>(out var s)
            && OrderMapper.Text(s).Trim() is { Length: > 0 } text ? text : null;

        var name = S("name") ?? order.BuyerName;
        var street1 = S("first_line") ?? "";
        var street2 = S("second_line");
        var city = S("city") ?? "";
        var rawState = S("state") ?? "";
        var state = NormalizeState(rawState);
        var zip = S("zip");
        var countryIso = S("country_iso")?.ToUpperInvariant() ?? "US";

        var isUs = IsUsJurisdiction(countryIso, state);

        return new ParsedAddress(
            RecipientName: name,
            Street1: street1,
            Street2: street2,
            City: city,
            State: state,
            Zip: zip,
            CountryIso: countryIso,
            IsUs: isUs);
    }

    public static string NormalizeState(string rawState)
    {
        var trimmed = rawState.Trim();
        if (trimmed.Length == 2)
            return trimmed.ToUpperInvariant();

        if (StateNamesToCodes.TryGetValue(trimmed, out var code))
            return code;

        return trimmed.ToUpperInvariant();
    }

    public static string ComputeAddressHash(ParsedAddress addr)
    {
        var zip5 = GetZip5(addr.Zip);
        var canonical = $"{Clean(addr.Street1)}|{Clean(addr.Street2)}|{Clean(addr.City)}|{Clean(addr.State)}|{zip5}|{Clean(addr.CountryIso)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string GetZip5(string? zip)
    {
        if (string.IsNullOrWhiteSpace(zip))
            return "";
        var clean = zip.Trim();
        var dash = clean.IndexOf('-');
        return dash > 0 ? clean[..dash].Trim() : (clean.Length >= 5 ? clean[..5] : clean);
    }

    private static string Clean(string? s) =>
        PunctuationRegex().Replace(s ?? "", "").Trim().ToUpperInvariant();

    private static bool IsKnownForeignCountry(string line)
    {
        var clean = line.Trim().ToUpperInvariant();
        return clean switch
        {
            "CANADA" or "UNITED KINGDOM" or "AUSTRALIA" or "GERMANY" or "FRANCE" or "JAPAN"
            or "NETHERLANDS" or "ITALY" or "SPAIN" or "MEXICO" or "BRAZIL" or "NEW ZEALAND"
            or "IRELAND" or "SWEDEN" or "NORWAY" or "DENMARK" or "FINLAND" or "SWITZERLAND"
            or "AUSTRIA" or "BELGIUM" or "POLAND" or "PORTUGAL" or "SINGAPORE" => true,
            _ => false
        };
    }
}
