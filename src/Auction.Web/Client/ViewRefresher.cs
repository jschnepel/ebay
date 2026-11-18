using Microsoft.JSInterop;

namespace Auction.Web.Client;

/// <summary>
/// Keeps an open view correct when live hints are lost (RT-02): it calls the view's refresh every
/// 10 seconds, when the tab becomes visible again, and after the live connection reconnects. Each page
/// owns one and disposes it when it closes.
/// </summary>
public sealed class ViewRefresher(LiveUpdates live, IJSRuntime js) : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();
    private Func<Task>? _refresh;
    private DotNetObjectReference<ViewRefresher>? _self;
    private IJSObjectReference? _visibility;

    public async Task StartAsync(Func<Task> refresh)
    {
        _refresh = refresh;
        live.Reconnected += refresh;
        _ = TickAsync(_stop.Token);
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/visibility.js");
            _self = DotNetObjectReference.Create(this);
            _visibility = await module.InvokeAsync<IJSObjectReference>("onVisible", _self);
        }
        catch (JSException)
        {
            // Without the visibility hook the timer still reconciles.
        }
    }

    [JSInvokable]
    public Task OnVisibleAsync() => _refresh?.Invoke() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_refresh is not null)
        {
            live.Reconnected -= _refresh;
        }

        if (_visibility is not null)
        {
            try
            {
                await _visibility.InvokeVoidAsync("dispose", CancellationToken.None);
                await _visibility.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is already gone.
            }
        }

        _self?.Dispose();
        _stop.Dispose();
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_refresh is not null)
                {
                    await _refresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The view closed.
        }
    }
}
