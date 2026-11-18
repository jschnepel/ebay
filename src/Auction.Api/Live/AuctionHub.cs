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
internal sealed class AuctionHub : Hub
{
    public const string Path = "/hubs/auctions";
    public const string ChangedMethod = "changed";
    public const string DirectoryGroup = "directory";

    public static string GroupFor(Guid auctionId) => $"auction:{auctionId:N}";

    [HubMethodName("Watch")]
    public Task WatchAsync(Guid auctionId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(auctionId));

    [HubMethodName("Unwatch")]
    public Task UnwatchAsync(Guid auctionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(auctionId));

    [HubMethodName("WatchDirectory")]
    public Task WatchDirectoryAsync() => Groups.AddToGroupAsync(Context.ConnectionId, DirectoryGroup);

    [HubMethodName("UnwatchDirectory")]
    public Task UnwatchDirectoryAsync() => Groups.RemoveFromGroupAsync(Context.ConnectionId, DirectoryGroup);
}
