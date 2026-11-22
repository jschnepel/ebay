using Auction.Api.Accounts;
using Auction.Api.Admin;
using Auction.Api.Auctions;
using Auction.Api.Images;
using Auction.Api.Live;
using Auction.Api.Notifications;
using Auction.Contracts.V1;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Auction.Api.Hosting;

internal static class ApiHosting
{
    public const string SessionCookie = "auction_session";

    public static WebApplicationBuilder AddApi(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders().AddJsonConsole(json => json.IncludeScopes = true);
        builder.AddTelemetry();

        builder.Services.AddOptions<ApiOptions>()
            .Bind(builder.Configuration.GetSection("Api"))
            .Validate(
                options => options.IsValid(),
                "Api options are missing or out of range: Api:EngineAddress is required, Api:PathBase must start with / and not end with one, Api:KeysDirectory must be an absolute path, "
                + "Api:TrustedProxies must be IP addresses or CIDR ranges, and the limits and intervals must be positive.")
            .ValidateOnStart();
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<ApiOptions>>().Value);

        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IConfiguration>().GetConnectionString("Api")
            ?? throw new InvalidOperationException("ConnectionStrings:Api is not set.")));
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<NotificationStore>();
        builder.Services.AddSingleton<Caching.RedisJobs>();
        builder.Services.AddSingleton<Caching.SharedCounters>();
        builder.Services.AddSingleton<Caching.AuctionCache>();
        builder.Services.AddSingleton<SignInGuard>();
        builder.Services.AddOptions<Images.ImageOptions>().Bind(builder.Configuration.GetSection("Images"))
            .Validate(options => options.IsValid(), "Images options are out of range.").ValidateOnStart();
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<Images.ImageOptions>>().Value);
        builder.Services.AddSingleton<Images.ImageStore>();
        builder.Services.AddHostedService<Images.ImageSweeper>();
        builder.Services.AddSingleton<AuctionReader>();
        builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();

        builder.Services.AddGrpcClient<AuctionCommandService.AuctionCommandServiceClient>((services, client) =>
            client.Address = services.GetRequiredService<ApiOptions>().EngineAddress);
        builder.Services.AddTransient<EngineGateway>();
        builder.Services.AddGrpcClient<Grpc.Health.V1.Health.HealthClient>((services, client) =>
            client.Address = services.GetRequiredService<ApiOptions>().EngineAddress);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<AdminKpis>();
        builder.Services.AddSingleton<AdminReader>();
        builder.Services.AddSingleton<RedisHealth>();
        builder.Services.AddHttpClient<SimulatorControl>(SimulatorControl.Configure);
        builder.Services.AddSingleton<Presence>();
        builder.Services.AddHostedService(services => services.GetRequiredService<Presence>());

        builder.Services.AddProblemDetails();
        if (builder.Configuration["Api:KeysDirectory"] is { Length: > 0 } keysDirectory)
        {
            // A fixed application name keeps the keys valid when the path base or the container changes.
            builder.Services.AddDataProtection()
                .SetApplicationName("auction-api")
                .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        }

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = SessionCookie;
            // Scoped to the path base, so the cookie never reaches the other apps on the same host.
            cookie.Cookie.Path = builder.Configuration["Api:PathBase"] is { Length: > 0 } pathBase ? pathBase : "/";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(7);
            cookie.SlidingExpiration = true;
            // An API answers with a status, never a redirect to a sign-in page.
            cookie.Events.OnRedirectToLogin = context => SetStatusAsync(context.Response, StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = context => SetStatusAsync(context.Response, StatusCodes.Status403Forbidden);
            // A revoked session (signed in elsewhere, signed out) or a disabled account fails its next request.
            cookie.Events.OnValidatePrincipal = async context =>
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionStore>();
                if (context.Principal?.UserIdOf() is not { } user
                    || context.Principal.SessionOf() is not { } session
                    || !await sessions.IsValidAsync(session, user, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddOpenApi();

        // Live hints go out through the Redis backplane whenever Redis is configured, even on one instance,
        // so a Redis outage stops hints (pages then reconcile on their timer) but never bidding.
        var signalR = builder.Services.AddSignalR();
        if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            signalR.AddStackExchangeRedis(redis, options => options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("auction"));
        }

        builder.Services.AddHostedService<OutboxRelay>();
        builder.Services.AddHealthChecks().AddReadiness();
        builder.Services.AddSingleton<AppPage>();
        return builder;
    }

    public static WebApplication MapApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ApiOptions>();
        if (options.TrustedProxyAddresses.Count + options.TrustedProxyNetworks.Count > 0)
        {
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var proxy in options.TrustedProxyAddresses)
            {
                forwarded.KnownProxies.Add(proxy);
            }

            foreach (var network in options.TrustedProxyNetworks)
            {
                forwarded.KnownIPNetworks.Add(network);
            }

            app.UseForwardedHeaders(forwarded);
        }

        if (options.PathBase.Length > 0)
        {
            app.UsePathBase(options.PathBase);
        }

        // Routing runs after the path base is taken off, so routes match the path inside the app.
        app.UseRouting();

        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            // API request latency for the admin KPIs; the client's static files aren't counted.
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                await next(context);
                return;
            }

            var started = TimeProvider.System.GetTimestamp();
            try
            {
                await next(context);
            }
            finally
            {
                context.RequestServices.GetRequiredService<AdminKpis>().RecordApi(TimeProvider.System.GetElapsedTime(started));
            }
        });
        app.UseStatusCodePages();
        app.Use(SecurityHeaders.AddAsync);
        app.UseMiddleware<SameOriginJsonMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/api/v1");
        api.MapAccounts();
        api.MapAuctions();
        api.MapAdmin();
        api.MapNotifications();
        api.MapImages();
        app.MapHub<AuctionHub>(AuctionHub.Path);
        app.MapOpenApi();
        // Liveness answers without touching any dependency; readiness needs PostgreSQL and the engine.
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains(Readiness.Tag) });

        // The Blazor client: its files, and its page for every other path so deep links load the app.
        // An unknown API path is still a 404 problem, never the app's page.
        app.MapStaticAssets();
        app.MapFallback("/api/{**path}", () => Problems.Of(StatusCodes.Status404NotFound, "not_found", "There is no such API path."));
        app.MapFallback((Delegate)AppPage.ServeAsync);
        return app;
    }

    private static Task SetStatusAsync(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}
