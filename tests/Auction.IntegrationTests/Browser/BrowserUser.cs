using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The steps a person takes in the browser, written once so each journey reads as a story.</summary>
internal static partial class BrowserUser
{
    /// <summary>
    /// Creates a new demo account from the sign-in page (as a user, or with <paramref name="role"/> "Admin" an
    /// admin), waits until the header shows it, and returns its email.
    /// </summary>
    public static async Task<string> SignUpAsync(this IPage page, string displayName, string role = "User")
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        await page.GotoAsync("/sign-in");
        await page.OpenCreateAccountAsync();
        await page.GetByLabel("Display name").FillAsync(displayName);
        await page.GetByLabel("Email for the new account").FillAsync(email);
        await page.GetByRole(AriaRole.Radio, new() { Name = role }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveTextAsync(displayName);
        return email;
    }

    /// <summary>On the sign-in page, switches from signing in to creating an account.</summary>
    public static Task OpenCreateAccountAsync(this IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

    /// <summary>Lists an item from the Sell area and publishes it; returns the auction's page path.</summary>
    public static async Task<string> ListAndPublishAsync(this IPage page, string title, string openingDollars, string reserveDollars)
    {
        await page.GotoAsync("/sell/new");
        await page.GetByLabel("Title").FillAsync(title);
        await page.GetByLabel("Description").FillAsync("Listed from the browser test.");
        await page.GetByLabel("Opening price ($)").FillAsync(openingDollars);
        await page.GetByLabel("Reserve ($)").FillAsync(reserveDollars);
        await page.SaveDraftAsync();

        var row = page.GetByTestId("my-auction-row").Filter(new() { HasText = title });
        await Assertions.Expect(row).ToContainTextAsync("Draft");
        await row.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("sell-result")).ToContainTextAsync("Published");
        await Assertions.Expect(row).ToContainTextAsync("Active");

        await row.GetByRole(AriaRole.Link, new() { Name = title }).ClickAsync();
        await page.WaitForURLAsync(AuctionPath());
        return new Uri(page.Url).AbsolutePath;
    }

    /// <summary>Saves the listing form, which stays open on the saved draft, then goes to the seller's listings.</summary>
    public static async Task SaveDraftAsync(this IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("draft-saved")).ToBeVisibleAsync();
        await page.GotoAsync("/sell");
    }

    public static async Task BidAsync(this IPage page, string dollars)
    {
        await page.GetByLabel(new Regex("^Your (maximum|bid)")).FillAsync(dollars);
        await page.GetByRole(AriaRole.Button, new() { Name = "Place bid" }).ClickAsync();
    }

    /// <summary>On a fixed-step auction, bids the next step with the card's one button.</summary>
    public static Task BidNextStepAsync(this IPage page) => page.GetByTestId("bid-next-step").ClickAsync();

    /// <summary>The directory row for <paramref name="title"/>, showing more pages until it appears.</summary>
    public static async Task<ILocator> DirectoryRowAsync(this IPage page, string title)
    {
        var row = page.GetByTestId("auction-row").Filter(new() { HasText = title });
        var more = page.GetByRole(AriaRole.Button, new() { Name = "Show more" });
        await Assertions.Expect(page.GetByTestId("auction-row").First).ToBeVisibleAsync();
        while (await row.CountAsync() == 0 && await more.CountAsync() > 0)
        {
            var shown = await page.GetByTestId("auction-row").CountAsync();
            await more.ClickAsync();
            await Assertions.Expect(page.GetByTestId("auction-row")).Not.ToHaveCountAsync(shown);
        }

        return row;
    }

    [GeneratedRegex(@"/auctions/[0-9a-f-]{36}$")]
    private static partial Regex AuctionPath();
}
