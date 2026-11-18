using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.RateLimiting;
using Auction.Api.Accounts;
using Microsoft.AspNetCore.RateLimiting;

namespace Auction.Api.Hosting;

/// <summary>The configured limits (§8 Login, Sign-up and Write rate limiting); tests and the simulator set their own.</summary>
public sealed class RateLimitOptions
{
    public int SignInPerAccountPerMinute { get; init; } = 10;

    public int SignInPerAddressPerMinute { get; init; } = 60;

    public int SignUpPerAddressPerHour { get; init; } = 20;

    public int WritesPerUserPerMinute { get; init; } = 300;

    /// <summary>Failed password attempts before an account is locked, and for how long.</summary>
    public int LockoutFailures { get; init; } = 5;

    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(5);

    public bool IsValid() =>
        SignInPerAccountPerMinute > 0 && SignInPerAddressPerMinute > 0 && SignUpPerAddressPerHour > 0 && WritesPerUserPerMinute > 0
        && LockoutFailures > 0 && LockoutDuration > TimeSpan.Zero;
}

/// <summary>
/// Request limits, answered with 429 and a Retry-After. Sign-up and sign-in are limited per client address
/// (the proxy's forwarded address once HOST-01 trusts it); every write is limited per signed-in user. These
/// counters are per instance.
/// </summary>
internal static class RateLimits
{
    public const string SignUp = "sign-up";
    public const string SignIn = "sign-in";
    public const string Write = "write";

    public static IServiceCollection AddApiRateLimits(this IServiceCollection services)
    {
        services.AddSingleton<SignInGuard>();
        return services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await Problems.Of(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests; try again shortly.")
                    .ExecuteAsync(context.HttpContext);
            };
            limiter.AddPolicy(SignUp, http => RateLimitPartition.GetFixedWindowLimiter(Address(http), _ => Window(Limits(http).SignUpPerAddressPerHour, TimeSpan.FromHours(1))));
            limiter.AddPolicy(SignIn, http => RateLimitPartition.GetFixedWindowLimiter(Address(http), _ => Window(Limits(http).SignInPerAddressPerMinute, TimeSpan.FromMinutes(1))));
            limiter.AddPolicy(Write, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.UserIdOf()?.Value.ToString() ?? Address(http), _ => Window(Limits(http).WritesPerUserPerMinute, TimeSpan.FromMinutes(1))));
        });
    }

    private static RateLimitOptions Limits(HttpContext http) => http.RequestServices.GetRequiredService<ApiOptions>().RateLimits;

    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) =>
        new() { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true };
}

/// <summary>
/// Per-account sign-in limits: at most so many attempts a minute, and a lockout after repeated wrong
/// passwords, so guessing one account's password is slow whichever addresses it comes from.
/// </summary>
internal sealed class SignInGuard(ApiOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Attempts> _accounts = new(StringComparer.Ordinal);

    private sealed class Attempts
    {
        public readonly Lock Gate = new();
        public DateTimeOffset WindowStart;
        public int InWindow;
        public int Failures;
        public DateTimeOffset LockedUntil;
    }

    /// <summary>Counts an attempt; false when the account is locked or over its per-minute limit.</summary>
    public bool TryAttempt(string normalizedEmail)
    {
        var now = clock.GetUtcNow();
        var attempts = _accounts.GetOrAdd(normalizedEmail, _ => new Attempts());
        lock (attempts.Gate)
        {
            if (now < attempts.LockedUntil)
            {
                return false;
            }

            if (now - attempts.WindowStart >= TimeSpan.FromMinutes(1))
            {
                (attempts.WindowStart, attempts.InWindow) = (now, 0);
            }

            return ++attempts.InWindow <= options.RateLimits.SignInPerAccountPerMinute;
        }
    }

    public void Failed(string normalizedEmail)
    {
        var attempts = _accounts.GetOrAdd(normalizedEmail, _ => new Attempts());
        lock (attempts.Gate)
        {
            if (++attempts.Failures >= options.RateLimits.LockoutFailures)
            {
                (attempts.Failures, attempts.LockedUntil) = (0, clock.GetUtcNow() + options.RateLimits.LockoutDuration);
            }
        }
    }

    public void Succeeded(string normalizedEmail)
    {
        if (_accounts.TryGetValue(normalizedEmail, out var attempts))
        {
            lock (attempts.Gate)
            {
                attempts.Failures = 0;
            }
        }
    }
}
