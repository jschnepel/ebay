using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
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
internal static partial class AccountEndpoints
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
        var guard = http.RequestServices.GetRequiredService<SignInGuard>();
        var events = http.RequestServices.GetRequiredService<AccountEvents>();
        var typed = TypedEmail(body.Email);

        // The limits count under the same text the account is looked up by, so no other spelling of it
        // (padding past the cut, say) reaches the account under a counter of its own.
        var key = AccountStore.Normalize(typed ?? "");

        // Nothing here stops when the client hangs up: a request that reached the API is decided and logged.
        if (!await guard.TryAttemptAsync(key))
        {
            // Logged once a minute for each email, so a refused flood costs the database nothing more.
            if (await guard.FirstRefusalAsync(key))
            {
                var known = typed is null ? null : await accounts.FindAsync(typed, CancellationToken.None);
                await events.RecordAsync(AccountEvents.SignInFailed, known?.Id.Value, Loggable(typed, known), "too_many_attempts", null);
            }

            http.Response.Headers.RetryAfter = "60";
            return Problems.Of(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many sign-in attempts for this account; try again later.");
        }

        var account = typed is null ? null : await accounts.FindAsync(typed, CancellationToken.None);
        var recorded = Loggable(typed, account);
        if (account is null || account.Disabled || !MaySignIn(account, body.Password, hasher, options))
        {
            if (!string.IsNullOrEmpty(body.Password))
            {
                await guard.FailedAsync(key);
            }

            await events.RecordAsync(
                AccountEvents.SignInFailed, account?.Id.Value, recorded, FailureCause(account, body.Password, recorded is not null), null);

            // The same answer whether the email is unknown or the password is wrong.
            return Problems.Of(StatusCodes.Status401Unauthorized, "sign_in_failed", "The email or password is incorrect.");
        }

        await guard.SucceededAsync(key);
        await IssueCookieAsync(http, account, AccountEvents.SignIn, recorded);
        return TypedResults.Ok(ToResponse(account));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        if (http.User.SessionOf() is { } session)
        {
            await http.RequestServices.GetRequiredService<SessionStore>().RevokeAsync(session, CancellationToken.None);
            await http.RequestServices.GetRequiredService<AccountEvents>().RecordAsync(
                AccountEvents.SignOut, http.User.UserIdOf()?.Value, null, null, session);
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

    // Why a sign-in failed, for the event log; the visitor is only ever told that the email or password is wrong.
    private static string FailureCause(Account? account, string? password, bool isEmail) => account switch
    {
        null when !isEmail => "not_an_email",
        null => "unknown_email",
        { Disabled: true } => "account_disabled",
        { PasswordHash: null } => "email_sign_in_off",
        _ when string.IsNullOrEmpty(password) => "no_password_given",
        _ => "wrong_password",
    };

    // The email as typed, trimmed and cut to the longest an address can be. Text with a control character or half
    // of a surrogate pair isn't an address and can't be sent to the database, so it counts as nothing typed.
    private static string? TypedEmail(string? email)
    {
        if (email?.Trim() is not { Length: > 0 } trimmed)
        {
            return null;
        }

        var cut = trimmed[..Math.Min(trimmed.Length, EmailMaxLength)];
        return cut.Any(char.IsControl) || char.IsHighSurrogate(cut[^1]) ? null : cut;
    }

    // What the event log keeps of the typed text: an account's own address, or else only text shaped like a
    // plain address (name@host.tld, no quotes or spaces). Anything else may be a password typed in the wrong
    // box; one that happens to look like an address can't be told apart and is kept.
    private static string? Loggable(string? typed, Account? account) =>
        typed is not null && (account is not null || PlainAddress().IsMatch(typed)) ? typed : null;

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PlainAddress();

    // One active session per account (AUTH-02): this sign-in ends every other one, and any page still open on
    // an ended session is told at once. The event log gets the sign-in and each session it replaced.
    private static async Task IssueCookieAsync(HttpContext http, Account account, string eventType, string? email)
    {
        var (session, ended) = await http.RequestServices.GetRequiredService<SessionStore>().StartAsync(account.Id, CancellationToken.None);
        var events = http.RequestServices.GetRequiredService<AccountEvents>();
        await events.RecordAsync(eventType, account.Id.Value, email, null, session);
        await events.RecordManyAsync(AccountEvents.SessionReplaced, ended, account.Id.Value, null);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, Principal.For(account, session), new AuthenticationProperties { IsPersistent = true });
        await Live.SignOuts.TellAsync(http.RequestServices, ended, CancellationToken.None);
    }

    private static bool IsEmail(string email) =>
        email.Length <= EmailMaxLength && MailAddress.TryCreate(email, out var address) && address.Address == email;

    private static AccountResponse ToResponse(Account account) => new(account.Id.Value, account.DisplayName, account.Kind);
}
