using System.Net.Mail;
using System.Security.Claims;
using Auction.Api.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace Auction.Api.Accounts;

/// <summary>
/// Sign-up, sign-in and sign-out (AUTH-01). Under DEMO_MODE a visitor signs up with a display name and an
/// email and signs back in with the email alone; seeded, simulator and admin accounts always need their
/// password. Outside DEMO_MODE only password accounts sign in.
/// </summary>
internal static class AccountEndpoints
{
    public const int DisplayNameMaxLength = 50;
    public const int EmailMaxLength = 254;

    public sealed record SignUpRequest(string? DisplayName, string? Email);

    public sealed record SignInRequest(string? Email, string? Password);

    public sealed record AccountResponse(Guid Id, string DisplayName, string Kind);

    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder api)
    {
        var account = api.MapGroup("/account").WithTags("Account");
        account.MapPost("/sign-up", SignUpAsync)
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        account.MapPost("/sign-in", SignInAsync)
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        account.MapPost("/sign-out", (Delegate)SignOutAsync).RequireAuthorization().Produces(StatusCodes.Status204NoContent);
        account.MapGet("/me", Me).RequireAuthorization().Produces<AccountResponse>();
        return api;
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest body, HttpContext http, AccountStore accounts, ApiOptions options, CancellationToken cancellationToken)
    {
        if (!options.DemoMode)
        {
            return Problems.Of(StatusCodes.Status403Forbidden, "sign_up_closed", "Self-registration is available only in the demo.");
        }

        var displayName = body.DisplayName?.Trim() ?? "";
        var email = body.Email?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (displayName.Length is 0 or > DisplayNameMaxLength)
        {
            errors["displayName"] = [displayName.Length == 0 ? "required" : "too_long"];
        }

        if (!IsEmail(email))
        {
            errors["email"] = ["invalid"];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        // Sign-up takes no Idempotency-Key: a retry after a lost response gets 409, and signing in with
        // the same email reaches the account the first attempt created.
        var account = await accounts.RegisterAsync(email, displayName, cancellationToken);
        if (account is null)
        {
            return Problems.Of(
                StatusCodes.Status409Conflict, "email_taken", "An account with this email already exists. Sign in with it instead.");
        }

        await IssueCookieAsync(http, account);
        return TypedResults.Created("/api/v1/account/me", ToResponse(account));
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest body, HttpContext http, AccountStore accounts, IPasswordHasher<Account> hasher, ApiOptions options,
        CancellationToken cancellationToken)
    {
        var account = string.IsNullOrWhiteSpace(body.Email) ? null : await accounts.FindAsync(body.Email, cancellationToken);
        if (account is null || account.Disabled || !MaySignIn(account, body.Password, hasher, options))
        {
            // The same answer whether the email is unknown or the password is wrong.
            return Problems.Of(StatusCodes.Status401Unauthorized, "sign_in_failed", "The email or password is incorrect.");
        }

        await IssueCookieAsync(http, account);
        return TypedResults.Ok(ToResponse(account));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static Ok<AccountResponse> Me(ClaimsPrincipal user) =>
        TypedResults.Ok(new AccountResponse(user.UserIdOf()!.Value.Value, user.Identity?.Name ?? "", user.KindOf()));

    private static bool MaySignIn(Account account, string? password, IPasswordHasher<Account> hasher, ApiOptions options) =>
        string.IsNullOrEmpty(password)
            ? options.DemoMode && account.Kind == AccountKinds.SelfRegistered && account.PasswordHash is null
            : account.PasswordHash is { } hash && hasher.VerifyHashedPassword(account, hash, password) != PasswordVerificationResult.Failed;

    private static Task IssueCookieAsync(HttpContext http, Account account) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, Principal.For(account), new AuthenticationProperties { IsPersistent = true });

    private static bool IsEmail(string email) =>
        email.Length <= EmailMaxLength && MailAddress.TryCreate(email, out var address) && address.Address == email;

    private static AccountResponse ToResponse(Account account) => new(account.Id.Value, account.DisplayName, account.Kind);
}
