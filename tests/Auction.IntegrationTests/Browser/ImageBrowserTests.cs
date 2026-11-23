using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>UI-05 in the browser: a seller adds a photo to a draft, publishes, and the directory shows it as the cover.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class ImageBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task A_listing_with_several_photos_shows_them_one_at_a_time_with_thumbnails_and_a_larger_view()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var title = $"Gallery item {Guid.NewGuid():N}"[..21];
        await seller.SignUpAsync("Gallery seller");
        await seller.GotoAsync("/sell/new");
        await seller.GetByLabel("Title").FillAsync(title);
        await seller.GetByLabel("Opening price ($)").FillAsync("5.00");
        await seller.GetByLabel("Reserve ($)").FillAsync("6.00");
        await seller.SaveDraftAsync();
        var row = seller.GetByTestId("my-auction-row").Filter(new() { HasText = title });
        await row.GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();
        foreach (var (name, type, image) in new[] { ("a.png", "image/png", TestImages.Png()), ("b.jpg", "image/jpeg", TestImages.Jpeg()), ("c.webp", "image/webp", TestImages.WebP()) })
        {
            var shown = await seller.GetByTestId("draft-image").CountAsync();
            await seller.GetByLabel("Add an image").SetInputFilesAsync(new FilePayload { Name = name, MimeType = type, Buffer = image });
            await Assertions.Expect(seller.GetByTestId("draft-image")).ToHaveCountAsync(shown + 1);
        }

        await seller.GotoAsync("/sell");
        await row.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Assertions.Expect(row).ToContainTextAsync("Active");
        await row.GetByRole(AriaRole.Link, new() { Name = title }).ClickAsync();

        // One photo at a time, with a thumbnail for each and where you are among them.
        var main = seller.GetByTestId("gallery-main").GetByRole(AriaRole.Img);
        var thumbs = seller.GetByTestId("gallery-thumb");
        await Assertions.Expect(thumbs).ToHaveCountAsync(3);
        await Assertions.Expect(seller.GetByTestId("gallery-count")).ToHaveTextAsync("1 of 3");
        await Assertions.Expect(main).ToHaveAttributeAsync("alt", $"{title}, photo 1 of 3");
        var first = await main.GetAttributeAsync("src");

        await thumbs.Nth(2).ClickAsync();
        await Assertions.Expect(seller.GetByTestId("gallery-count")).ToHaveTextAsync("3 of 3");
        await Assertions.Expect(main).Not.ToHaveAttributeAsync("src", first!);
        await Assertions.Expect(thumbs.Nth(2)).ToHaveAttributeAsync("aria-current", "true");

        // Next goes round to the first; the arrow keys do the same from the photo.
        await seller.GetByRole(AriaRole.Button, new() { Name = "Next photo" }).ClickAsync();
        await Assertions.Expect(seller.GetByTestId("gallery-count")).ToHaveTextAsync("1 of 3");
        await seller.GetByTestId("gallery-main").FocusAsync();
        await seller.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(seller.GetByTestId("gallery-count")).ToHaveTextAsync("3 of 3");

        // The photo opens larger and closes with Escape.
        await seller.GetByTestId("gallery-main").ClickAsync();
        var larger = seller.GetByRole(AriaRole.Dialog, new() { Name = "Photos" });
        await Assertions.Expect(larger.GetByRole(AriaRole.Img)).ToHaveAttributeAsync("alt", $"{title}, photo 3 of 3");
        await seller.Keyboard.PressAsync("Escape");
        await Assertions.Expect(larger).ToHaveCountAsync(0);
    }

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
        await seller.SaveDraftAsync();
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
