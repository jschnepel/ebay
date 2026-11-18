using System.Collections.Concurrent;

namespace Auction.Api.Live;

/// <summary>
/// Signed-in users with a live connection to this instance, for the admin activity view. A user with
/// several tabs counts once.
/// </summary>
internal sealed class Presence
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public int OnlineUsers => _connections.Count;

    public void Connected(Guid userId) => _connections.AddOrUpdate(userId, 1, (_, count) => count + 1);

    public void Disconnected(Guid userId)
    {
        while (_connections.TryGetValue(userId, out var count))
        {
            var removed = count <= 1
                ? _connections.TryRemove(new KeyValuePair<Guid, int>(userId, count))
                : _connections.TryUpdate(userId, count - 1, count);
            if (removed)
            {
                return;
            }
        }
    }
}
