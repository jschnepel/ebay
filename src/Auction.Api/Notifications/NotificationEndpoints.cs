using Auction.Api.Accounts;
using Auction.Api.Auctions;
using Auction.Api.Hosting;

namespace Auction.Api.Notifications;

/// <summary>The signed-in user's own notifications (NOT-01): the newest first, and marking one read.</summary>
internal static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotifications(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/me/notifications").WithTags("Me").RequireAuthorization();
        notifications.MapGet("", ListAsync).Produces<NotificationPage>().ProducesProblem(StatusCodes.Status400BadRequest);
        notifications.MapPost("/{id:long}/read", MarkReadAsync).RequireRateLimiting(RateLimits.Write)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> ListAsync(
        int? pageSize, string? cursor, HttpContext http, NotificationStore store, ApiOptions options)
    {
        var size = Math.Min(pageSize ?? options.DefaultPageSize, options.MaxPageSize);
        if (size < 1)
        {
            return Problems.BadRequest("invalid_page_size", "pageSize must be at least 1.");
        }

        if (!Cursors.TryDecode(cursor, out long? before))
        {
            return Problems.BadRequest("invalid_cursor", "The cursor is not one this API returned.");
        }

        return TypedResults.Ok(await store.PageAsync(http.User.UserIdOf()!.Value, before, size, http.RequestAborted));
    }

    // Someone else's notification is "not found", as if it didn't exist.
    private static async Task<IResult> MarkReadAsync(long id, HttpContext http, NotificationStore store) =>
        await store.MarkReadAsync(http.User.UserIdOf()!.Value, id, http.RequestAborted)
            ? TypedResults.NoContent()
            : Problems.Of(StatusCodes.Status404NotFound, "not_found", "No such notification.");
}
