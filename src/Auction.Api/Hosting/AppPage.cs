namespace Auction.Api.Hosting;

/// <summary>
/// The client's page for every non-API path, so a deep link loads the app. Its <c>&lt;base href&gt;</c> is set
/// to the path base, so every relative link, API call and hub connection in the client stays under it.
/// </summary>
internal sealed class AppPage
{
    private readonly Lock _gate = new();
    private string? _page;

    public static Task<IResult> ServeAsync(HttpContext http) => http.RequestServices.GetRequiredService<AppPage>().ServePageAsync(http);

    private async Task<IResult> ServePageAsync(HttpContext http)
    {
        var page = _page;
        if (page is null)
        {
            var file = http.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo("index.html");
            if (!file.Exists)
            {
                return Problems.Of(StatusCodes.Status404NotFound, "not_found", "The client isn't built.");
            }

            await using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            var pathBase = http.RequestServices.GetRequiredService<ApiOptions>().PathBase;
            page = (await reader.ReadToEndAsync(http.RequestAborted)).Replace("<base href=\"/\" />", $"<base href=\"{pathBase}/\" />", StringComparison.Ordinal);
            lock (_gate)
            {
                _page ??= page;
            }
        }

        // The page itself must never be cached, so a new release's files are picked up at once.
        http.Response.Headers.CacheControl = "no-cache";
        return Results.Content(page, "text/html; charset=utf-8");
    }
}
