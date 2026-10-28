using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

public class UsAddressParserTests
{
    [Theory]
    [InlineData("US", "CA", true)]
    [InlineData("USA", "NY", true)]
    [InlineData("United States", "TX", true)]
    [InlineData("US", "DC", true)]
    [InlineData(null, "California", true)]
    [InlineData(null, "District of Columbia", true)]
    [InlineData("US", "PR", true)]
    [InlineData("PR", null, true)]
    [InlineData(null, "Puerto Rico", true)]
    [InlineData("US", "GU", true)]
    [InlineData(null, "Guam", true)]
    [InlineData("US", "VI", true)]
    [InlineData(null, "Virgin Islands", true)]
    [InlineData("US", "AS", true)]
    [InlineData("US", "MP", true)]
    [InlineData("US", "UM", true)]
    [InlineData("US", "AE", true)]
    [InlineData("US", "AP", true)]
    [InlineData("US", "AA", true)]
    [InlineData("CA", "ON", false)]
    [InlineData("Canada", "QC", false)]
    [InlineData("GB", "ENG", false)]
    [InlineData("UK", null, false)]
    [InlineData("DE", "BY", false)]
    [InlineData("FR", "IDF", false)]
    [InlineData("AU", "NSW", false)]
    public void IsUsJurisdiction_identifies_US_states_territories_and_military(string? country, string? state, bool expected)
    {
        Assert.Equal(expected, UsAddressParser.IsUsJurisdiction(country, state));
    }

    [Fact]
    public void ParseOverrideText_parses_standard_3_line_address()
    {
        var text = """
        Jane Doe
        123 Main St
        Springfield, IL 62704
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("Jane Doe", parsed.RecipientName);
        Assert.Equal("123 Main St", parsed.Street1);
        Assert.Null(parsed.Street2);
        Assert.Equal("Springfield", parsed.City);
        Assert.Equal("IL", parsed.State);
        Assert.Equal("62704", parsed.Zip);
        Assert.Equal("US", parsed.CountryIso);
    }

    [Fact]
    public void ParseOverrideText_parses_4_line_address_with_unit()
    {
        var text = """
        John Smith
        742 Evergreen Terrace
        Apt 2B
        Springfield, OR 97477-1234
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("John Smith", parsed.RecipientName);
        Assert.Equal("742 Evergreen Terrace", parsed.Street1);
        Assert.Equal("Apt 2B", parsed.Street2);
        Assert.Equal("Springfield", parsed.City);
        Assert.Equal("OR", parsed.State);
        Assert.Equal("97477-1234", parsed.Zip);
    }

    [Fact]
    public void ParseOverrideText_strips_trailing_United_States()
    {
        var text = """
        Mary Johnson
        100 Maple Ave
        Austin, TX 78701
        United States
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("Mary Johnson", parsed.RecipientName);
        Assert.Equal("100 Maple Ave", parsed.Street1);
        Assert.Equal("Austin", parsed.City);
        Assert.Equal("TX", parsed.State);
        Assert.Equal("78701", parsed.Zip);
        Assert.Equal("US", parsed.CountryIso);
    }

    [Fact]
    public void ParseOverrideText_flags_trailing_foreign_country()
    {
        var text = """
        Pierre Dubois
        123 Rue Principale
        Montreal, QC H2X 1Y6
        Canada
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.False(parsed.IsUs);
        Assert.Equal("Pierre Dubois", parsed.RecipientName);
        Assert.Equal("CANADA", parsed.CountryIso);
    }

    [Fact]
    public void ParseOverrideText_parses_military_APO_address()
    {
        var text = """
        SGT Robert Miller
        PSC 1234 Box 5678
        APO, AE 09012
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("SGT Robert Miller", parsed.RecipientName);
        Assert.Equal("PSC 1234 Box 5678", parsed.Street1);
        Assert.Equal("APO", parsed.City);
        Assert.Equal("AE", parsed.State);
        Assert.Equal("09012", parsed.Zip);
    }

    [Fact]
    public void ParseOverrideText_normalizes_full_state_name_to_code()
    {
        var text = """
        Bob Vance
        100 Industrial Way
        Scranton, Pennsylvania 18503
        """;

        var parsed = UsAddressParser.ParseOverrideText(text);

        Assert.True(parsed.IsUs);
        Assert.Equal("PA", parsed.State);
    }

    [Fact]
    public void ParseFromReceipt_extracts_structured_fields()
    {
        var order = new Order
        {
            BuyerName = "Alice Cooper",
            RawJson = """
            {
                "name": "Alice Cooper",
                "first_line": "500 Rock Way",
                "second_line": "Suite 100",
                "city": "Phoenix",
                "state": "Arizona",
                "zip": "85001",
                "country_iso": "US"
            }
            """
        };

        var parsed = UsAddressParser.ParseFromReceipt(order);

        Assert.True(parsed.IsUs);
        Assert.Equal("Alice Cooper", parsed.RecipientName);
        Assert.Equal("500 Rock Way", parsed.Street1);
        Assert.Equal("Suite 100", parsed.Street2);
        Assert.Equal("Phoenix", parsed.City);
        Assert.Equal("AZ", parsed.State);
        Assert.Equal("85001", parsed.Zip);
    }

    [Fact]
    public void ComputeAddressHash_is_deterministic_and_insensitive_to_case_and_punctuation()
    {
        var addr1 = new ParsedAddress(
            RecipientName: "Buyer One",
            Street1: "123 Main St.",
            Street2: "Apt #4B",
            City: "Springfield",
            State: "IL",
            Zip: "62704-1234",
            CountryIso: "US",
            IsUs: true);

        var addr2 = new ParsedAddress(
            RecipientName: "Different Name", // Name is excluded from hash
            Street1: "123 main st",
            Street2: "apt 4b",
            City: "SPRINGFIELD",
            State: "il",
            Zip: "62704", // zip5 is used
            CountryIso: "us",
            IsUs: true);

        var hash1 = UsAddressParser.ComputeAddressHash(addr1);
        var hash2 = UsAddressParser.ComputeAddressHash(addr2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeAddressHash_changes_when_address_components_change()
    {
        var baseAddr = new ParsedAddress("Buyer", "123 Main St", null, "Springfield", "IL", "62704", "US", true);
        var baseHash = UsAddressParser.ComputeAddressHash(baseAddr);

        var diffStreet = baseAddr with { Street1 = "124 Main St" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffStreet));

        var diffCity = baseAddr with { City = "Decatur" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffCity));

        var diffZip = baseAddr with { Zip = "62705" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffZip));

        var diffState = baseAddr with { State = "IN" };
        Assert.NotEqual(baseHash, UsAddressParser.ComputeAddressHash(diffState));
    }
}
