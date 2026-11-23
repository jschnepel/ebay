using System.Net.Mail;
using System.Security.Claims;
using Auction.Api.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace Auction.Api.Accounts;

/// <summary>
/// Sign-up, sign-in and sign-out (AUTH-01). Under DEMO_MODE a visitor signs up with a display name, an email
/// and a role (user, the default, or admin) and signs back in with the email alone; seeded, simulator and the
/// seeded admin account always need their password. Outside DEMO_MODE only password accounts sign in.
/// </summary>
internal static class AccountEndpoints
{
    public const int DisplayNameMaxLength = 50;
    public const int EmailMaxLength = 254;

    public sealed record SignUpRequest(string? DisplayName, string? Email, string? Role);

    public sealed record SignInRequest(string? Email, string? Password);

    public sealed record AccountResponse(Guid Id, string DisplayName, string Kind);

    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder api)
    {
        var account = api.MapGroup("/account").WithTags("Account");
        account.MapPost("/sign-up", SignUpAsync).Limited(RateLimits.Policy.SignUp)
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
        account.MapPost("/sign-in", SignInAsync).Limited(RateLimits.Policy.SignIn)
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
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

        var kind = body.Role switch
        {
            null or "user" => AccountKinds.SelfRegistered,
            "admin" => AccountKinds.VisitorAdmin,
            _ => null,
        };
        if (kind is null)
        {
            errors["role"] = ["invalid"];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        // Sign-up takes no Idempotency-Key: a retry after a lost response gets 409, and signing in with
        // the same email reaches the account the first attempt created.
        var account = await accounts.RegisterAsync(email, displayName, kind!, cancellationToken);
        if (account is null)
        {
            return Problems.Of(
                StatusCodes.Status409Conflict, "email_taken", "An account with this email already exists. Sign in with it instead.");
        }

        await IssueCookieAsync(http, account, AccountEvents.SignUp, email);
        return TypedResults.Created("/api/v1/account/me", ToResponse(account));
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest body, HttpContext http, AccountStore accounts, IPasswordHasher<Account> hasher, ApiOptions options)
    {
        var key = AccountStore.Normalize(body.Email ?? "");
        var guard = http.RequestServices.GetRequiredService<SignInGuard>();
        var typed = TypedEmail(body.Email);
        if (!await guard.TryAttemptAsync(key))
        {
            http.Response.Headers.RetryAfter = "60";
            return Problems.Of(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many sign-in attempts for this account; try again later.");
        }

        var account = typed is null ? null : await accounts.FindAsync(typed, http.RequestAborted);
        if (account is null || account.Disabled || !MaySignIn(account, body.Password, hasher, options))
        {
            if (!string.IsNullOrEmpty(body.Password))
            {
                await guard.FailedAsync(key);
            }

            // The same answer whether the email is unknown or the password is wrong.
            return Problems.Of(StatusCodes.Status401Unauthorized, "sign_in_failed", "The email or password is incorrect.");
        }

        await guard.SucceededAsync(key);
        await IssueCookieAsync(http, account, AccountEvents.SignIn, typed);
        return TypedResults.Ok(ToResponse(account));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        if (http.User.SessionOf() is { } session)
        {
            await http.RequestServices.GetRequiredService<SessionStore>().RevokeAsync(session, http.RequestAborted);
            await http.RequestServices.GetRequiredService<AccountEvents>().RecordAsync(
                AccountEvents.SignOut, http.User.UserIdOf()?.Value, null, null, session, http.RequestAborted);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static Ok<AccountResponse> Me(ClaimsPrincipal user) =>
        TypedResults.Ok(new AccountResponse(user.UserIdOf()!.Value.Value, user.Identity?.Name ?? "", user.KindOf()));

    // An account a visitor made has no password and signs in by email alone, under DEMO_MODE; anything typed in
    // the password box by mistake is ignored rather than refused. Every other account needs its password.
    private static bool MaySignIn(Account account, string? password, IPasswordHasher<Account> hasher, ApiOptions options) =>
        account.PasswordHash is not { } hash
            ? options.DemoMode && AccountKinds.IsVisitor(account.Kind)
            : !string.IsNullOrEmpty(password) && hasher.VerifyHashedPassword(account, hash, password) != PasswordVerificationResult.Failed;

    // The email as typed, trimmed and cut to the column's length, for the event log.
    private static string? TypedEmail(string? email) =>
        email?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, EmailMaxLength)] : null;

    // One active session per account (AUTH-02): this sign-in ends every other one, and any page still open on
    // an ended session is told at once. The event log gets the sign-in and each session it replaced.
    private static async Task IssueCookieAsync(HttpContext http, Account account, string eventType, string? email)
    {
        var (session, ended) = await http.RequestServices.GetRequiredService<SessionStore>().StartAsync(account.Id, http.RequestAborted);
        var events = http.RequestServices.GetRequiredService<AccountEvents>();
        await events.RecordAsync(eventType, account.Id.Value, email, null, session, http.RequestAborted);
        await events.RecordManyAsync(AccountEvents.SessionReplaced, ended, account.Id.Value, null, http.RequestAborted);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, Principal.For(account, session), new AuthenticationProperties { IsPersistent = true });
        await Live.SignOuts.TellAsync(http.RequestServices, ended, http.RequestAborted);
    }

    private static bool IsEmail(string email) =>
        email.Length <= EmailMaxLength && MailAddress.TryCreate(email, out var address) && address.Address == email;

    private static AccountResponse ToResponse(Account account) => new(account.Id.Value, account.DisplayName, account.Kind);
}
