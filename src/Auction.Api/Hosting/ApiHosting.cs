using Auction.Api.Accounts;
using Auction.Api.Admin;
using Auction.Api.Auctions;
using Auction.Api.Live;
using Auction.Contracts.V1;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

        builder.Services.AddOptions<ApiOptions>()
            .Bind(builder.Configuration.GetSection("Api"))
            .Validate(options => options.IsValid(), "Api options are missing or out of range (Api:EngineAddress is required).")
            .ValidateOnStart();
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<ApiOptions>>().Value);

        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IConfiguration>().GetConnectionString("Api")
            ?? throw new InvalidOperationException("ConnectionStrings:Api is not set.")));
        builder.Services.AddSingleton<AccountStore>();
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
        builder.Services.AddSingleton<Presence>();

        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = SessionCookie;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(7);
            cookie.SlidingExpiration = true;
            // An API answers with a status, never a redirect to a sign-in page.
            cookie.Events.OnRedirectToLogin = context => SetStatusAsync(context.Response, StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = context => SetStatusAsync(context.Response, StatusCodes.Status403Forbidden);
            // A disabled (or deleted) account loses its session on its next request.
            cookie.Events.OnValidatePrincipal = async context =>
            {
                var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountStore>();
                if (context.Principal?.UserIdOf() is not { } user || !await accounts.IsActiveAsync(user, context.HttpContext.RequestAborted))
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
        builder.Services.AddHealthChecks();
        return builder;
    }

    public static WebApplication MapApi(this WebApplication app)
    {
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
        app.MapHub<AuctionHub>(AuctionHub.Path);
        app.MapOpenApi();
        app.MapHealthChecks("/health");

        // The Blazor client: its files, and its page for every other path so deep links load the app.
        // An unknown API path is still a 404 problem, never the app's page.
        app.MapStaticAssets();
        app.MapFallback("/api/{**path}", () => Problems.Of(StatusCodes.Status404NotFound, "not_found", "There is no such API path."));
        app.MapFallbackToFile("index.html");
        return app;
    }

    private static Task SetStatusAsync(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}
