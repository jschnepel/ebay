using Auction.Api.Accounts;
using Microsoft.AspNetCore.SignalR;

namespace Auction.Api.Live;

/// <summary>
/// A change hint: the auction and its new version, nothing else. A client that shows an older version
/// re-fetches the auction over REST, which stays authoritative and filters what each viewer may see.
/// </summary>
public sealed record AuctionChanged(Guid AuctionId, long Version);

/// <summary>
/// Live hints (RT-01). A browser watches the auctions it shows, or the directory for every published
/// change. Anyone may watch: hints carry only public facts, and drafts never produce events.
/// </summary>
internal sealed class AuctionHub(Presence presence) : Hub
{
    public const string Path = "/hubs/auctions";
    public const string ChangedMethod = "changed";
    public const string DirectoryGroup = "directory";
    public const string NotifiedMethod = "notified";
    public const string SignedOutMethod = "signedOut";

    /// <summary>Every connection of one user, for their notifications.</summary>
    public static string UserGroup(Guid userId) => $"user:{userId:N}";

    /// <summary>Every connection of one session, told when signing in elsewhere ends it.</summary>
    public static string SessionGroup(Guid sessionId) => $"session:{sessionId:N}";

    public static string GroupFor(Guid auctionId) => $"auction:{auctionId:N}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.UserIdOf() is { } user)
        {
            presence.Connected(user.Value);
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(user.Value));
            if (Context.User.SessionOf() is { } session)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(session));
            }
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User?.UserIdOf() is { } user)
        {
            presence.Disconnected(user.Value);
        }

        return base.OnDisconnectedAsync(exception);
    }

    [HubMethodName("Watch")]
    public Task WatchAsync(Guid auctionId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(auctionId));

    [HubMethodName("Unwatch")]
    public Task UnwatchAsync(Guid auctionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(auctionId));

    [HubMethodName("WatchDirectory")]
    public Task WatchDirectoryAsync() => Groups.AddToGroupAsync(Context.ConnectionId, DirectoryGroup);

    [HubMethodName("UnwatchDirectory")]
    public Task UnwatchDirectoryAsync() => Groups.RemoveFromGroupAsync(Context.ConnectionId, DirectoryGroup);
}
