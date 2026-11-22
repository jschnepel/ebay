using Auction.Transport;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Auction.Engine.Hosting;

internal static class EngineHosting
{
    public static WebApplicationBuilder AddEngine(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders().AddJsonConsole(json => json.IncludeScopes = true);

        builder.Services.AddSingleton<PeerIdentity>();
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var identity = kestrel.ApplicationServices.GetRequiredService<PeerIdentity>();
            kestrel.ListenAnyIP(context.Configuration.GetValue("Engine:Port", 8080), endpoint => ConfigureTls(endpoint, identity));
        });

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

        builder.Services.AddGrpc(grpc => grpc.Interceptors.Add<CorrelationInterceptor>())
            .AddServiceOptions<AuctionCommandGrpcService>(grpc => grpc.Interceptors.Add<CommandAuthorization>());
        builder.Services.AddGrpcHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");
        return builder;
    }

    private static void ConfigureTls(ListenOptions endpoint, PeerIdentity identity)
    {
        endpoint.Protocols = HttpProtocols.Http2;
        endpoint.UseHttps(https =>
        {
            https.ServerCertificate = identity.Certificate;
            // Only health probes may omit the client certificate.
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (peer, _, _) => identity.Trusts(peer);
        });
    }

    public static WebApplication MapEngine(this WebApplication app)
    {
        app.MapGrpcService<AuctionCommandGrpcService>();
        app.MapGrpcHealthChecksService();
        return app;
    }
}
