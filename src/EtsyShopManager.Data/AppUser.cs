using Microsoft.AspNetCore.Identity;

namespace EtsyShopManager.Data;

/// <summary>Local user account (ASP.NET Core Identity).</summary>
public class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}
