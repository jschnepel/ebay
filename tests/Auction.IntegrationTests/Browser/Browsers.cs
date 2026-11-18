using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Auction.IntegrationTests.Browser.Browsers))]

namespace Auction.IntegrationTests.Browser;

/// <summary>
/// One headless Chromium for the browser tests, installed and launched on first use. Each test user gets
/// its own browser context, so cookies never leak between them. Set <c>AUCTION_BASE_URL</c> to run the
/// same tests against a running stack (the Compose one-command run) instead of the in-process site.
/// </summary>
public sealed class Browsers : IAsyncLifetime
{
    public const string BaseUrlVariable = "AUCTION_BASE_URL";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public ValueTask InitializeAsync()
    {
        // Explicit bounds (the 2-second A5 checks) stay as written; everything else allows a slow machine.
        Assertions.SetDefaultExpectTimeout(10_000);
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

    /// <summary>The site under test: <c>AUCTION_BASE_URL</c> if set, otherwise the in-process one.</summary>
    public static async Task<Uri> SiteAsync(MigratedDatabase db) =>
        Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 } url ? new Uri(url) : await db.SiteAsync();

    /// <summary>A fresh context (its own cookies) with one page, at the site's base address.</summary>
    public async Task<IPage> NewPageAsync(Uri site)
    {
        var browser = await BrowserAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = site.ToString() });
        context.SetDefaultTimeout(15_000);
        return await context.NewPageAsync();
    }

    private async Task<IBrowser> BrowserAsync()
    {
        await _gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_browser is null)
            {
                // Downloads Chromium once per machine; a no-op when it's already there.
                var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                Assert.Equal(0, exit);
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            }

            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// The browser tests run together, after the parallel tests, so their time bounds are measured without the
/// rest of the suite competing for the machine.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserJourneys
{
    public const string Name = "Browser journeys";

    private BrowserJourneys()
    {
    }
}
