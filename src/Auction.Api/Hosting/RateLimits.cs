using System.Globalization;
using Auction.Api.Accounts;
using Auction.Api.Caching;

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
/// Request limits, answered with 429 and Retry-After, counted in Redis so they hold across API instances
/// (per instance while Redis is down). Sign-up and sign-in are limited per client address (the proxy's
/// forwarded address once it is trusted); every write is limited per signed-in user.
/// </summary>
internal static class RateLimits
{
    public enum Policy
    {
        SignUp,
        SignIn,
        Write,
    }

    /// <summary>Applies a policy to an endpoint or every endpoint in a group.</summary>
    public static TBuilder Limited<TBuilder>(this TBuilder builder, Policy policy)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var limits = http.RequestServices.GetRequiredService<ApiOptions>().RateLimits;
            var (key, permits, window) = policy switch
            {
                Policy.SignUp => ($"sign-up:{Address(http)}", limits.SignUpPerAddressPerHour, TimeSpan.FromHours(1)),
                Policy.SignIn => ($"sign-in:{Address(http)}", limits.SignInPerAddressPerMinute, TimeSpan.FromMinutes(1)),
                _ => ($"write:{http.User.UserIdOf()?.Value.ToString() ?? Address(http)}", limits.WritesPerUserPerMinute, TimeSpan.FromMinutes(1)),
            };
            var (count, retryAfter) = await http.RequestServices.GetRequiredService<SharedCounters>().IncrementAsync(key, window);
            if (count <= permits)
            {
                return await next(context);
            }

            http.Response.Headers.RetryAfter = Seconds(retryAfter);
            return Problems.Of(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests; try again shortly.");
        });

    public static string Seconds(TimeSpan wait) => ((int)Math.Ceiling(Math.Max(1, wait.TotalSeconds))).ToString(CultureInfo.InvariantCulture);

    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>
/// Per-account sign-in limits, shared across instances: at most so many attempts a minute, and a lockout
/// after repeated wrong passwords, so guessing one account's password is slow whichever addresses it comes from.
/// </summary>
internal sealed class SignInGuard(SharedCounters counters, ApiOptions options)
{
    /// <summary>Counts an attempt; false when the account is locked or over its per-minute limit.</summary>
    public async Task<bool> TryAttemptAsync(string normalizedEmail)
    {
        if (await counters.HasFlagAsync($"locked:{normalizedEmail}"))
        {
            return false;
        }

        var (count, _) = await counters.IncrementAsync($"sign-in-account:{normalizedEmail}", TimeSpan.FromMinutes(1));
        return count <= options.RateLimits.SignInPerAccountPerMinute;
    }

    public async Task FailedAsync(string normalizedEmail)
    {
        var (failures, _) = await counters.IncrementAsync($"sign-in-failed:{normalizedEmail}", options.RateLimits.LockoutDuration);
        if (failures >= options.RateLimits.LockoutFailures)
        {
            await counters.SetFlagAsync($"locked:{normalizedEmail}", options.RateLimits.LockoutDuration);
            await counters.ClearAsync($"sign-in-failed:{normalizedEmail}", options.RateLimits.LockoutDuration);
        }
    }

    public Task SucceededAsync(string normalizedEmail) =>
        counters.ClearAsync($"sign-in-failed:{normalizedEmail}", options.RateLimits.LockoutDuration);
}
