using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>UI-05 in the browser: a seller adds a photo to a draft, publishes, and the directory shows it as the cover.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class ImageBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task A_seller_uploads_a_photo_and_the_directory_shows_it_as_the_cover()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var title = $"Photo item {Guid.NewGuid():N}"[..19];
        await seller.SignUpAsync("Photo seller");

        await seller.GotoAsync("/sell/new");
        await seller.GetByLabel("Title").FillAsync(title);
        await seller.GetByLabel("Opening price ($)").FillAsync("5.00");
        await seller.GetByLabel("Reserve ($)").FillAsync("6.00");
        await seller.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        await seller.WaitForURLAsync("**/sell");
        var row = seller.GetByTestId("my-auction-row").Filter(new() { HasText = title });
        await row.GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();

        await seller.GetByLabel("Add an image").SetInputFilesAsync(new FilePayload { Name = "photo.png", MimeType = "image/png", Buffer = TestImages.Png() });
        await Assertions.Expect(seller.GetByTestId("draft-image")).ToHaveCountAsync(1);
        await Assertions.Expect(seller.GetByTestId("draft-image").GetByText("Cover")).ToBeVisibleAsync();

        await seller.GotoAsync("/sell");
        await row.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Assertions.Expect(row).ToContainTextAsync("Active");

        var buyer = await browsers.NewPageAsync(site);
        await buyer.GotoAsync("/");
        var listed = await buyer.DirectoryRowAsync(title);
        var cover = listed.GetByTestId("cover");
        await Assertions.Expect(cover).ToBeVisibleAsync();
        Assert.True(await cover.EvaluateAsync<int>("img => img.naturalWidth") > 0, "the cover image loaded");
    }
}
