using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Auction.StackTests.StackUnderTest))]

namespace Auction.StackTests;

/// <summary>
/// The running Compose stack under test. eng/e2e.sh starts it under its own project name and passes its
/// address, the seeded admin's password and the Compose project and env file in the environment. Without
/// them every test is skipped with a note saying how to run it.
/// </summary>
public sealed class StackUnderTest : IAsyncLifetime
{
    public const string UrlVariable = "AUCTION_STACK_URL";
    public const string AdminPasswordVariable = "AUCTION_ADMIN_PASSWORD";
    public const string ProjectVariable = "AUCTION_COMPOSE_PROJECT";
    public const string EnvFileVariable = "AUCTION_COMPOSE_ENV_FILE";
    public const string AdminEmail = "admin@auction.test";

    private const string HowToRun = "No stack under test: run eng/e2e.sh, which starts one and sets " + UrlVariable + ".";

    private readonly Uri? _site = Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? new Uri(url) : null;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The stack's published address.</summary>
    public Uri Site => _site ?? throw SkipException();

    public static string AdminPassword => Required(AdminPasswordVariable);

    public ValueTask InitializeAsync()
    {
        Assertions.SetDefaultExpectTimeout(15_000);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }

    /// <summary>A client with its own cookies, like one visitor's browser.</summary>
    public HttpClient Client() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = Site };

    /// <summary>A new self-registered account, signed in on its own client.</summary>
    public async Task<StackUser> SignUpAsync(string displayName)
    {
        var http = Client();
        using var response = await http.PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName, email = $"{Guid.NewGuid():N}@example.test" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new StackUser(http, account.GetProperty("id").GetGuid());
    }

    /// <summary>Waits until the API reports itself ready (PostgreSQL and the engine reachable).</summary>
    public async Task WaitUntilReadyAsync()
    {
        using var http = Client();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                using var ready = await http.GetAsync(new Uri("/ready", UriKind.Relative), Ct);
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
                // The container is still starting.
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The stack did not report ready within 90 seconds.");
            await Task.Delay(500, Ct);
        }
    }

    /// <summary>A fresh browser context (its own cookies) with one page, at the stack's address.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var site = Site;
        await _gate.WaitAsync(Ct);
        try
        {
            if (_browser is null)
            {
                // Downloads Chromium once per machine; a no-op when it's already there.
                Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            }
        }
        finally
        {
            _gate.Release();
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = site.ToString() });
        context.SetDefaultTimeout(20_000);
        return await context.NewPageAsync();
    }

    /// <summary>Runs <c>docker compose</c> against the stack under test and returns what it printed.</summary>
    public Task<string> ComposeAsync(params string[] args)
    {
        _ = Site;
        var file = Path.Combine(RepositoryRoot(), "deploy", "compose.yaml");
        return DockerAsync(["compose", "-p", Required(ProjectVariable), "--env-file", Required(EnvFileVariable), "-f", file, .. args]);
    }

    /// <summary>The id of the container running <paramref name="service"/> (or the one that last ran it).</summary>
    public async Task<string> ContainerOfAsync(string service) =>
        Assert.Single((await ComposeAsync("ps", "-a", "-q", service)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static async Task<string> DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("docker did not start.");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', args)} exited {process.ExitCode}: {await error}");
        return await output;
    }

    private static string Required(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : throw SkipException();

    private static UnreachableException SkipException()
    {
        Assert.Skip(HowToRun);
        return new UnreachableException();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Auction.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Auction.slnx) was not found above the test output.");
    }
}

/// <summary>A signed-in account and the client that carries its session cookie.</summary>
public sealed record StackUser(HttpClient Http, Guid Id)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates and publishes an hour-long auction that is active at once; returns its id.</summary>
    public async Task<Guid> PublishAsync(string title, long openingCents = 1000, long reserveCents = 1500)
    {
        var now = DateTimeOffset.UtcNow;
        using var draft = await CommandAsync("/api/v1/auctions", new
        {
            title,
            description = "Listed by the end-to-end tests.",
            openingPriceCents = openingCents,
            incrementCents = 100,
            floorType = "reserve",
            reserveCents,
            bidEntry = "proxy",
            startsAt = now.AddMinutes(-1),
            endsAt = now.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var id = (await draft.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("auctionId").GetGuid();
        using var published = await CommandAsync($"/api/v1/auctions/{id}/publish", body: null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return id;
    }

    public Task<HttpResponseMessage> BidAsync(Guid auction, long amountCents) =>
        CommandAsync($"/api/v1/auctions/{auction}/bids", new { amountCents });

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Http.GetAsync(new Uri(path, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> CommandAsync(string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Http.SendAsync(request, Ct);
    }
}

/// <summary>
/// Every stack test runs alone: some restart containers, and the live checks have a two-second bound that
/// a busy machine would blur.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StackRuns
{
    public const string Name = "Stack";

    private StackRuns()
    {
    }
}
