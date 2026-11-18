using Microsoft.Net.Http.Headers;

namespace Auction.Api.Hosting;

/// <summary>
/// Cross-site request protection without antiforgery tokens (ADR-0003): an unsafe request is refused if it
/// comes from a foreign <c>Origin</c> (403) or carries a body that isn't JSON (415). A cross-site form can't
/// send JSON without a CORS preflight, which the API never approves, and the session cookie is SameSite=Strict.
/// </summary>
internal sealed class SameOriginJsonMiddleware(RequestDelegate next, ApiOptions options, IProblemDetailsService problems)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (IsSafe(request.Method))
        {
            await next(context);
        }
        else if (request.Headers.Origin is { Count: > 0 } origin && !IsAllowed(origin.ToString(), request))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "foreign_origin", "Cross-site requests are not accepted.");
        }
        else if (!IsJsonOrEmpty(request))
        {
            await RefuseAsync(context, StatusCodes.Status415UnsupportedMediaType, "json_only", "Send the request body as application/json.");
        }
        else
        {
            await next(context);
        }
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private bool IsAllowed(string origin, HttpRequest request) =>
        string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
        || options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    private static bool IsJsonOrEmpty(HttpRequest request) =>
        request.ContentType is null
            ? request.ContentLength is null or 0
            : MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
              && type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private async Task RefuseAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }
}
