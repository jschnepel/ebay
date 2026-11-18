using System.Security.Claims;
using Auction.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Auction.Api.Accounts;

/// <summary>
/// The signed-in user, as carried in the session cookie. Every command's actor comes from here, never
/// from a request body.
/// </summary>
internal static class Principal
{
    public const string KindClaim = "kind";

    public static ClaimsPrincipal For(Account account) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.Value.ToString()),
                new Claim(ClaimTypes.Name, account.DisplayName),
                new Claim(KindClaim, account.Kind),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>The signed-in user's id, or null for an anonymous request.</summary>
    public static UserId? UserIdOf(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? new UserId(id) : null;

    public static string KindOf(this ClaimsPrincipal user) => user.FindFirstValue(KindClaim) ?? "";

    /// <summary>Admin accounts have admin privileges only: they never bid, sell or publish.</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) => user.KindOf() == AccountKinds.Admin;
}
