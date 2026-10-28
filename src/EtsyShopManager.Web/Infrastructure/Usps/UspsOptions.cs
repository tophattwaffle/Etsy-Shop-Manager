namespace EtsyShopManager.Web.Infrastructure.Usps;

public class UspsOptions
{
    public string ApiBaseUrl { get; set; } = "https://apis.usps.com";
    public string OAuthUrl { get; set; } = "https://apis.usps.com/oauth2/v3/token";
}
