using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Auction.Contracts.V1;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Grpc.Health.V1;
using Grpc.Net.Client;

namespace Auction.IntegrationTests.EngineService;

/// <summary>
/// The engine running in a container built from src/Auction.Engine/Dockerfile, reached by a real gRPC
/// client over the mapped port, exactly as the API will reach it.
/// </summary>
public sealed class EngineServiceContainer : IAsyncDisposable
{
    private const ushort GrpcPort = 8080;

    // Built once per test run and shared by every engine container.
    private static readonly SemaphoreSlim ImageGate = new(1, 1);
    private static IFutureDockerImage? _image;

    private readonly IContainer _container;
    private readonly GrpcChannel _channel;
    private readonly X509Certificate2 _identity = X509CertificateLoader.LoadPkcs12(
        Convert.FromBase64String(TestIdentity.Api.Identity), null, X509KeyStorageFlags.EphemeralKeySet);

    private EngineServiceContainer(IContainer container)
    {
        _container = container;
        Address = new Uri($"https://{container.Hostname}:{container.GetMappedPublicPort(GrpcPort)}");
        _channel = Channel(_identity);
        Commands = new AuctionCommandService.AuctionCommandServiceClient(_channel);
        Health = new Health.HealthClient(_channel);
    }

    /// <summary>The engine's gRPC address from the host, for clients such as the API under test.</summary>
    public Uri Address { get; }

    public AuctionCommandService.AuctionCommandServiceClient Commands { get; }

    public Health.HealthClient Health { get; }

    public static async Task<EngineServiceContainer> StartAsync(INetwork network, string connectionString)
    {
        var image = await ImageAsync();
        var container = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithEnvironment("ConnectionStrings__Engine", connectionString)
            .WithEnvironment("EngineTls__Identity", TestIdentity.Engine.Identity)
            .WithEnvironment("EngineTls__PeerSha256", TestIdentity.Api.Pin)
            .WithEnvironment("Engine__CloseWorkerEnabled", "false")
            .WithEnvironment("Engine__LockTimeout", "00:00:10")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on"))
            .Build();
        await container.StartAsync();
        return new EngineServiceContainer(container);
    }

    public GrpcChannel ChannelWithoutIdentity() => Channel(null);

    private GrpcChannel Channel(X509Certificate2? identity)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == TestIdentity.Engine.Pin,
        };
        if (identity is not null)
        {
            handler.ClientCertificates.Add(identity);
        }

        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("x-auction-role", "participant");
        return GrpcChannel.ForAddress(Address, new GrpcChannelOptions { HttpClient = http, DisposeHttpClient = true });
    }

    public async Task<string> LogsAsync() => (await _container.GetLogsAsync()).Stdout;

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _identity.Dispose();
        await _container.DisposeAsync();
    }

    private static async Task<IFutureDockerImage> ImageAsync()
    {
        await ImageGate.WaitAsync();
        try
        {
            if (_image is null)
            {
                var image = new ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(RepositoryRoot())
                    .WithDockerfile("src/Auction.Engine/Dockerfile")
                    .WithName($"auction-engine-test:{Guid.NewGuid():N}")
                    .WithCleanUp(true)
                    .Build();
                await image.CreateAsync();
                _image = image;
            }

            return _image;
        }
        finally
        {
            ImageGate.Release();
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auction.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Auction.slnx not found above the test output.");
    }
}
