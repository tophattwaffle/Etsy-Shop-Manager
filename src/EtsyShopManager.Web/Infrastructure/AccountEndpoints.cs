using EtsyShopManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace EtsyShopManager.Web.Infrastructure;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Form post from the app bar. Binding [FromForm] makes the antiforgery middleware validate the token.
        endpoints.MapPost("/account/logout", async (SignInManager<AppUser> signInManager, HttpContext http, [FromForm] string? returnUrl) =>
            {
                await signInManager.SignOutAsync();
                return Results.LocalRedirect($"{http.Request.PathBase}/account/login");
            })
            .RequireAuthorization()
            .ExcludeFromDescription();
    }
}
