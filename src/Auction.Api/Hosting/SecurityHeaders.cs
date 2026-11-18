namespace Auction.Api.Hosting;

/// <summary>
/// Response headers for every request. The content security policy lets the page run only this origin's
/// scripts (WebAssembly needs <c>'wasm-unsafe-eval'</c>, never <c>'unsafe-eval'</c>), call only this
/// origin, and never be framed. MudBlazor sets inline styles, so styles allow <c>'unsafe-inline'</c>.
/// </summary>
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static Task AddAsync(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        // S7039 flags style-src 'unsafe-inline'; MudBlazor sets element styles inline, and scripts stay restricted.
#pragma warning disable S7039
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
#pragma warning restore S7039
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        return next();
    }
}
