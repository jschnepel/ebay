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
    public const string SessionClaim = "sid";

    public static ClaimsPrincipal For(Account account, Guid session) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.Value.ToString()),
                new Claim(ClaimTypes.Name, account.DisplayName),
                new Claim(KindClaim, account.Kind),
                new Claim(SessionClaim, session.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>The server-side session this cookie belongs to (AUTH-02).</summary>
    public static Guid? SessionOf(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SessionClaim), out var id) ? id : null;

    /// <summary>The signed-in user's id, or null for an anonymous request.</summary>
    public static UserId? UserIdOf(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? new UserId(id) : null;

    public static string KindOf(this ClaimsPrincipal user) => user.FindFirstValue(KindClaim) ?? "";

    /// <summary>Admin accounts have admin privileges only: they never bid, sell or publish.</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) => user.KindOf() == AccountKinds.Admin;
}
