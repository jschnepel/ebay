using Auction.Simulator.Live;

namespace Auction.IntegrationTests.Simulation;

/// <summary>The live marketplace's catalogue and its bundled photos: every photo exists, is credited with a free licence and fits the upload rules.</summary>
public sealed class CatalogueTests
{
    private static readonly string[] Categories =
        ["Books", "Cameras", "Collectibles", "Electronics", "Furniture", "Musical instruments", "Sports gear", "Watches"];

    [Fact]
    public void Every_category_has_at_least_five_items_with_sensible_price_ranges()
    {
        var items = Catalogue.Load(Catalogue.Directory);

        Assert.Equal(Categories, items.Select(i => i.Category).Distinct().Order(StringComparer.Ordinal));
        Assert.All(Categories, c => Assert.True(items.Count(i => i.Category == c) >= 5, c));
        Assert.All(items, i =>
        {
            Assert.InRange(i.Title.Length, 5, 80);
            Assert.InRange(i.Description.Length, 20, 400);
            Assert.True(i.PriceLowCents >= 500 && i.PriceHighCents > i.PriceLowCents, i.Title);
            Assert.NotEmpty(i.Photos);
        });
    }

    [Fact]
    public void Every_photo_exists_is_a_credited_free_jpeg_and_fits_the_upload_limits()
    {
        var items = Catalogue.Load(Catalogue.Directory);
        var credits = File.ReadAllLines(Path.Combine(Catalogue.Directory, "CREDITS.md"));
        var photos = items.SelectMany(i => i.Photos).Distinct().ToList();

        long total = 0;
        foreach (var photo in photos)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Catalogue.Directory, "photos", photo));
            Assert.True(bytes is [0xFF, 0xD8, 0xFF, ..], photo);
            Assert.True(bytes.Length <= 5_242_880, photo);
            total += bytes.Length;
            var credit = Assert.Single(credits, line => line.Contains($"`{photo}`", StringComparison.Ordinal));
            Assert.True(credit.Contains("CC0", StringComparison.Ordinal) || credit.Contains("Public domain", StringComparison.Ordinal), credit);
            Assert.Contains("https://", credit, StringComparison.Ordinal);
        }

        Assert.True(total < 20_000_000, $"{total:N0} bytes of photos");
        Assert.True(photos.Count >= 30, $"{photos.Count} photos");
    }
}
