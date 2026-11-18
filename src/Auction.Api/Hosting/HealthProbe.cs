namespace Auction.Api.Hosting;

/// <summary>
/// <c>dotnet Auction.Api.dll --health-check</c>: asks the running API's <c>/health</c> endpoint whether it
/// is serving. Built into the binary because the chiseled image has no shell or curl for a probe.
/// </summary>
internal static class HealthProbe
{
    public const string Argument = "--health-check";

    private static readonly char[] PortSeparators = [';', ','];

    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(PortSeparators)[0] ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
