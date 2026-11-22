using System.Text.Json;

namespace Auction.Simulator.Live;

/// <summary>One kind of thing the live marketplace sells: its category, words, typical price range and photos.</summary>
public sealed record CatalogueItem(string Category, string Title, string Description, long PriceLowCents, long PriceHighCents, IReadOnlyList<string> Photos);

/// <summary>
/// The live marketplace's catalogue: <c>Catalogue/catalogue.json</c> beside the simulator, with its photos in
/// <c>Catalogue/photos</c> (each credited in <c>Catalogue/CREDITS.md</c>).
/// </summary>
public static class Catalogue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Where the catalogue is copied with the build.</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Catalogue");

    public static IReadOnlyList<CatalogueItem> Load(string directory)
    {
        using var file = File.OpenRead(Path.Combine(directory, "catalogue.json"));
        return JsonSerializer.Deserialize<CatalogueFile>(file, Json)?.Items
            ?? throw new InvalidDataException("catalogue.json has no items.");
    }

    /// <summary>A photo's bytes, by its file name in the catalogue.</summary>
    public static byte[] Photo(string directory, string name) => File.ReadAllBytes(Path.Combine(directory, "photos", name));

    private sealed record CatalogueFile(IReadOnlyList<CatalogueItem> Items);
}
