using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace Auction.Web.Client;

/// <summary>A change hint from the server: this auction now has this version.</summary>
public sealed record AuctionChanged(Guid AuctionId, long Version);

/// <summary>
/// One SignalR connection per tab for live change hints. A hint only says "this auction changed"; the
/// page re-fetches over REST and keeps whichever version is newest. After a reconnect the watches are
/// restored and every page is told to reconcile, since hints sent while disconnected are lost.
/// </summary>
public sealed class LiveUpdates(NavigationManager navigation) : IAsyncDisposable
{
    // Watches are counted per page: navigating between two pages that watch the same thing never drops it,
    // whichever page's cleanup runs last.
    private readonly Dictionary<Guid, int> _auctions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private int _directory;

    /// <summary>A hint arrived.</summary>
    public event Func<AuctionChanged, Task>? Changed;

    /// <summary>The connection came back; hints may have been missed, so re-fetch.</summary>
    public event Func<Task>? Reconnected;

    /// <summary>A new notification arrived for the signed-in user.</summary>
    public event Func<Task>? Notified;

    /// <summary>This session ended because the account signed in somewhere else.</summary>
    public event Func<Task>? SignedOut;

    /// <summary>
    /// Opens the connection now (it otherwise opens on the first watch), so a signed-in user hears about
    /// notifications and sign-outs on every page.
    /// </summary>
    public Task ConnectAsync() => ConnectionAsync();

    /// <summary>
    /// Reconnects as whoever is now signed in: the server sorts a connection into its user's groups when it
    /// opens, so a sign-in or sign-out needs a fresh one. Watches are restored on it.
    /// </summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        var connection = await ConnectionAsync();
        await RestoreAsync(connection.ConnectionId);
    }

    public async Task WatchAsync(Guid auctionId)
    {
        var count = _auctions.GetValueOrDefault(auctionId) + 1;
        _auctions[auctionId] = count;
        if (count == 1)
        {
            await InvokeAsync("Watch", auctionId);
        }
    }

    public async Task UnwatchAsync(Guid auctionId)
    {
        var count = _auctions.GetValueOrDefault(auctionId) - 1;
        if (count > 0)
        {
            _auctions[auctionId] = count;
            return;
        }

        _auctions.Remove(auctionId);
        await InvokeAsync("Unwatch", auctionId);
    }

    public async Task WatchDirectoryAsync()
    {
        if (++_directory == 1)
        {
            await InvokeAsync("WatchDirectory", null);
        }
    }

    public async Task UnwatchDirectoryAsync()
    {
        if (--_directory == 0)
        {
            await InvokeAsync("UnwatchDirectory", null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    // Live hints are an optimisation: if the hub can't be reached, pages still reconcile on their timer.
    private async Task InvokeAsync(string method, Guid? auctionId)
    {
        try
        {
            var connection = await ConnectionAsync();
            if (connection.State != HubConnectionState.Connected)
            {
                return;
            }

            await (auctionId is { } id ? connection.InvokeAsync(method, id) : connection.InvokeAsync(method));
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Reconcile covers it.
        }
    }

    private async Task<HubConnection> ConnectionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(navigation.ToAbsoluteUri("hubs/auctions"))
                    .WithAutomaticReconnect(new KeepTrying())
                    .Build();
                _connection.On<AuctionChanged>("changed", hint => RaiseAsync(Changed, handler => handler(hint)));
                _connection.On("notified", () => RaiseAsync(Notified, handler => handler()));
                _connection.On("signedOut", () => RaiseAsync(SignedOut, handler => handler()));
                _connection.Reconnected += RestoreAsync;
                await _connection.StartAsync();
            }

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreAsync(string? connectionId)
    {
        foreach (var id in _auctions.Keys.ToArray())
        {
            await _connection!.InvokeAsync("Watch", id);
        }

        if (_directory > 0)
        {
            await _connection!.InvokeAsync("WatchDirectory");
        }

        await RaiseAsync(Reconnected, handler => handler());
    }

    // Every subscriber runs, one after another; an event's own Invoke would await only the last.
    private static async Task RaiseAsync<THandler>(THandler? handlers, Func<THandler, Task> call)
        where THandler : Delegate
    {
        foreach (var handler in handlers?.GetInvocationList().Cast<THandler>() ?? [])
        {
            await call(handler);
        }
    }

    /// <summary>Retries forever, backing off to every 5 seconds.</summary>
    private sealed class KeepTrying : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            TimeSpan.FromSeconds(Math.Min(5, retryContext.PreviousRetryCount));
    }
}
