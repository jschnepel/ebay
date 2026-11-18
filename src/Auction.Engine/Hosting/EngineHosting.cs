using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Auction.Engine.Hosting;

internal static class EngineHosting
{
    public static WebApplicationBuilder AddEngine(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders().AddJsonConsole(json => json.IncludeScopes = true);

        // gRPC over plaintext HTTP/2 on the internal network; the engine is never exposed publicly.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));

        builder.Services.AddOptions<EngineOptions>()
            .Bind(builder.Configuration.GetSection("Engine"))
            .Validate(options => options.IsValid(), "Engine options are out of range.")
            .ValidateOnStart();
        builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<EngineOptions>>().Value);

        var connectionString = builder.Configuration.GetConnectionString("Engine")
            ?? throw new InvalidOperationException("ConnectionStrings:Engine is not set.");
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.AddSingleton<AuctionEngine>();
        builder.Services.AddHostedService<CloseWorker>();

        builder.Services.AddGrpc(grpc => grpc.Interceptors.Add<CorrelationInterceptor>());
        builder.Services.AddGrpcHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");
        return builder;
    }

    public static WebApplication MapEngine(this WebApplication app)
    {
        app.MapGrpcService<AuctionCommandGrpcService>();
        app.MapGrpcHealthChecksService();
        return app;
    }
}
