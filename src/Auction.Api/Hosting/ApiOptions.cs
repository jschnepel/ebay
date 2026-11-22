namespace Auction.Api.Hosting;

/// <summary>The API's settings, bound from the <c>Api</c> section and validated at startup.</summary>
public sealed class ApiOptions
{
    /// <summary>
    /// The demo setting: visitors may self-register, and self-registered accounts sign in by email alone.
    /// Password accounts (seeded, simulator, admin) always need their password.
    /// </summary>
    public bool DemoMode { get; init; }

    /// <summary>The engine's gRPC address on the internal network.</summary>
    public Uri? EngineAddress { get; init; }

    /// <summary>The deadline on every engine call.</summary>
    public TimeSpan EngineDeadline { get; init; } = TimeSpan.FromSeconds(5);

    public int DefaultPageSize { get; init; } = 20;

    /// <summary>The most items any list returns; a larger requested page size is cut to this.</summary>
    public int MaxPageSize { get; init; } = 50;

    /// <summary>Origins accepted on unsafe requests besides the API's own scheme, host and port.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>How often the outbox relay looks for events when no notification woke it.</summary>
    public TimeSpan RelayPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the relay waits before trying again after a failure (database or Redis down).</summary>
    public TimeSpan RelayRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public RateLimitOptions RateLimits { get; init; } = new();

    /// <summary>
    /// The path the site is served under behind the portfolio's reverse proxy (for example /projects/ebay);
    /// empty serves it at the root. Pages, the API, the hub and the session cookie all live under it.
    /// </summary>
    public string PathBase { get; init; } = "";

    /// <summary>
    /// The reverse proxies whose X-Forwarded-For and X-Forwarded-Proto are believed (HOST-01), as
    /// comma-separated IP addresses. From anyone else the headers are ignored, so a client can't choose its
    /// own address for the rate limits.
    /// </summary>
    public string TrustedProxies { get; init; } = "";

    public IReadOnlyList<System.Net.IPAddress> TrustedProxyAddresses =>
        [.. TrustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(System.Net.IPAddress.Parse)];

    /// <summary>How close to its end an active auction counts as "ending soon" in the directory (UI-03).</summary>
    public TimeSpan EndingSoonWindow { get; init; } = TimeSpan.FromHours(1);

    public bool IsValid() =>
        EngineAddress is { IsAbsoluteUri: true } && EngineAddress.Scheme == Uri.UriSchemeHttps
        && EngineDeadline > TimeSpan.Zero
        && DefaultPageSize >= 1
        && MaxPageSize >= DefaultPageSize
        && RelayPollInterval > TimeSpan.Zero
        && RelayRetryDelay > TimeSpan.Zero
        && RateLimits.IsValid()
        && EndingSoonWindow > TimeSpan.Zero
        && (PathBase.Length == 0 || (PathBase.StartsWith('/') && !PathBase.EndsWith('/')))
        && TrustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(proxy => System.Net.IPAddress.TryParse(proxy, out _));
}
