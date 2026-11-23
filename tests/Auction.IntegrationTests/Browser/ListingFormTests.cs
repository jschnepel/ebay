using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>
/// The listing form in a browser: its rules are stated before they're hit, each problem is shown at its field,
/// and a listing is written, given photos and published without leaving the form.
/// </summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class ListingFormTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task The_form_states_its_limits_and_marks_each_field_that_needs_fixing()
    {
        var page = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await page.SignUpAsync("Careful seller");
        await page.GotoAsync("/sell/new");

        // The rules a seller would otherwise find by breaking them.
        await Assertions.Expect(Field(page, "Title")).ToContainTextAsync("3 to 120 characters");
        await Assertions.Expect(Field(page, "Opening price ($)")).ToContainTextAsync("$0.01 to $10,000,000");
        await Assertions.Expect(Field(page, "Reserve ($)")).ToContainTextAsync("At least the opening price");
        await Assertions.Expect(Field(page, "Ends")).ToContainTextAsync("a minute to 14 days");
        await Assertions.Expect(page.GetByText("can't be changed once you publish")).ToBeVisibleAsync();

        await page.GetByLabel("Description").FillAsync("Typed before the mistakes.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();

        // Each problem at its own field, all of them listed at the top, and nothing typed is lost.
        await Assertions.Expect(Field(page, "Title")).ToContainTextAsync("Enter a title.");
        await Assertions.Expect(Field(page, "Opening price ($)")).ToContainTextAsync("Enter an opening price in dollars, like 12.50.");
        await Assertions.Expect(Field(page, "Reserve ($)")).ToContainTextAsync("Enter a reserve in dollars, or choose another price floor.");
        var summary = page.GetByTestId("draft-error");
        await Assertions.Expect(summary.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByLabel("Description")).ToHaveValueAsync("Typed before the mistakes.");

        await page.GetByLabel("Title").FillAsync("ab");
        await page.GetByLabel("Opening price ($)").FillAsync("25");
        await page.GetByLabel("Reserve ($)").FillAsync("20");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();

        await Assertions.Expect(Field(page, "Title")).ToContainTextAsync("The title needs at least 3 characters.");
        await Assertions.Expect(Field(page, "Reserve ($)")).ToContainTextAsync("The reserve can't be below the opening price.");
        await Assertions.Expect(Field(page, "Opening price ($)")).Not.ToContainTextAsync("Enter an opening price");
    }

    [Fact]
    public async Task A_listing_is_written_given_a_photo_and_published_without_leaving_the_form()
    {
        var page = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        var title = $"One flow {Guid.NewGuid():N}"[..17];
        await page.SignUpAsync("One flow seller");
        await page.GotoAsync("/sell/new");
        await page.GetByLabel("Title").FillAsync(title);
        await page.GetByLabel("Opening price ($)").FillAsync("5.00");
        await page.GetByLabel("Reserve ($)").FillAsync("6.00");

        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();

        // Still in the form, now on the saved draft, where the photos are.
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/sell/[0-9a-f-]{36}/edit$"));
        await Assertions.Expect(page.GetByTestId("draft-saved")).ToContainTextAsync("Draft saved");
        await Assertions.Expect(page.GetByLabel("Title")).ToHaveValueAsync(title);
        await page.GetByLabel("Add an image").SetInputFilesAsync(new FilePayload { Name = "photo.png", MimeType = "image/png", Buffer = TestImages.Png() });
        await Assertions.Expect(page.GetByTestId("draft-image")).ToHaveCountAsync(1);

        // Publishing is asked about first, because the terms and photos are fixed from then on.
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish", Exact = true }).ClickAsync();
        var confirm = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(confirm).ToContainTextAsync("can't be changed");
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Keep as draft" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Title")).ToHaveValueAsync(title);

        await page.GetByRole(AriaRole.Button, new() { Name = "Publish", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Publish listing" }).ClickAsync();

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/auctions/[0-9a-f-]{36}$"));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("status")).ToContainTextAsync("Active");
        await Assertions.Expect(page.GetByTestId("gallery-main")).ToBeVisibleAsync();
    }

    // A field with everything shown around it: its label, hint and error.
    private static ILocator Field(IPage page, string label) =>
        page.Locator(".mud-input-control").Filter(new() { Has = page.GetByLabel(label, new() { Exact = true }) });
}
