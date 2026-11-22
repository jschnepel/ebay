using System.Collections.Concurrent;
using System.Text.Json;
using Auction.Verifier;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auction.Simulator.Live;

/// <summary>How busy the live marketplace keeps the site, and how often it acts.</summary>
public sealed record LiveOptions(int TargetListings = 40, int Sellers = 24, int Bidders = 50)
{
    /// <summary>How often the sellers check their listings and top them up.</summary>
    public TimeSpan Cycle { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the verifier checks every auction since the live marketplace started.</summary>
    public TimeSpan VerifyEvery { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Scales the bidders' pauses (tests act a hundred times faster).</summary>
    public double ThinkScale { get; init; } = 1.0; // not money: a factor on pauses

    /// <summary>At most this many new listings per cycle, so a refill is spread over a few cycles.</summary>
    public int MaxNewPerCycle { get; init; } = 10;

    /// <summary>
    /// At most this many sign-ins a minute, so the site's per-address sign-in limit (60 a minute, and every
    /// simulated account signs in from the simulator's one address) keeps room for an admin's short run.
    /// </summary>
    public int SignInsPerMinute { get; init; } = 30;

    public string CatalogueDirectory { get; init; } = Catalogue.Directory;
}

/// <summary>The live marketplace as the admin area shows it.</summary>
public sealed record LiveStatus(
    string State, DateTimeOffset? Since, DateTimeOffset? ResumesAt, int ListingsLive, int BidsAccepted, DateTimeOffset? LastVerifiedAt, bool? LastVerifyPassed);

/// <summary>
/// The always-on marketplace for the demo: simulated sellers keep about <see cref="LiveOptions.TargetListings"/>
/// photographed auctions live, and simulated bidders bid on every active auction (visitors' too) at a human
/// pace. Everything goes through the public API with the ordinary sign-in (SIM-05). Each cycle reads the
/// site's current state instead of trusting memory, so a cancelled listing is replaced, a disabled account
/// is skipped, a seller at its daily publish limit rests until the next UTC day, and after a demo reset the
/// marketplace signs in again and refills. No failure ends the loops: it is logged and the next cycle tries again.
/// </summary>
public sealed partial class LiveMarketplace : IDisposable
{
    private readonly LiveOptions _options;
    private readonly SimulationOptions _simulation;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<CatalogueItem> _catalogue;
    private readonly Dictionary<string, CatalogueItem> _byTitle;
    private readonly ConcurrentDictionary<string, byte[]> _photos = new();
    private readonly Seat[] _sellers;
    private readonly Seat[] _bidders;
    private readonly VirtualUser _browser;
    private readonly ConcurrentDictionary<Guid, CatalogueItem> _own = new();
    private readonly ConcurrentDictionary<(Guid Auction, int Bidder), long> _valuations = new();
    private readonly Queue<DateTimeOffset> _signIns = new();
    private readonly Lock _signInGate = new();
    private readonly TaskCompletionSource _firstCycle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<ListedAuction> _market = [];
    private int _listingsLive;
    private int _bidsAccepted;
    private DateTimeOffset? _since;
    private DateTimeOffset? _lastVerifiedAt;
    private bool? _lastVerifyPassed;
    private int _running;

    public LiveMarketplace(LiveOptions options, SimulationOptions simulation, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(simulation);
        if (options.Bidders > SimulatorAccounts.MaxBidders || options.Sellers > SimulatorAccounts.LiveSellers)
        {
            throw new ArgumentException($"At most {SimulatorAccounts.LiveSellers} sellers and {SimulatorAccounts.MaxBidders} bidders are seeded.", nameof(options));
        }

        (_options, _simulation, _time, _logger) = (options, simulation, time, logger);
        _catalogue = Catalogue.Load(options.CatalogueDirectory);
        _byTitle = _catalogue.ToDictionary(i => i.Title, StringComparer.Ordinal);
        _sellers = [.. Enumerable.Range(1, options.Sellers).Select(i => new Seat(SimulatorAccounts.LiveSellerEmail(i), simulation.BaseAddress))];
        _bidders = [.. Enumerable.Range(1, options.Bidders).Select(i => new Seat(SimulatorAccounts.BidderEmail(i), simulation.BaseAddress))];
        _browser = new VirtualUser(simulation.BaseAddress);
    }

    public bool Running => Volatile.Read(ref _running) == 1;

    public LiveStatus Status() => new(
        Running ? "running" : "stopped", _since, null, Volatile.Read(ref _listingsLive), Volatile.Read(ref _bidsAccepted), _lastVerifiedAt, _lastVerifyPassed);

    /// <summary>Runs until <paramref name="stop"/> is cancelled. It may be run again afterwards; its counts carry on.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        _since ??= _time.GetUtcNow();
        Volatile.Write(ref _running, 1);
        try
        {
            await Task.WhenAll(
                [ListingLoopAsync(stop), VerifyLoopAsync(stop), .. _bidders.Select((seat, i) => BidLoopAsync(seat, i, stop))]);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        foreach (var seat in _sellers.Concat(_bidders))
        {
            seat.User.Dispose();
        }

        _browser.Dispose();
    }

    private async Task ListingLoopAsync(CancellationToken stop)
    {
        // Seeded on purpose, so a marketplace can be repeated; nothing here is security-sensitive.
#pragma warning disable S2245
        var planner = new LivePlanner(new Random(_simulation.Seed));
#pragma warning restore S2245
        while (!stop.IsCancellationRequested)
        {
            await GuardedAsync(() => ListingCycleAsync(planner, stop), stop);
            // Bidders start after the sellers' first cycle, so the sellers sign in first.
            _firstCycle.TrySetResult();
            await DelayAsync(_options.Cycle, stop);
        }
    }

    private async Task ListingCycleAsync(LivePlanner planner, CancellationToken stop)
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var live = 0;
        var liveTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in _sellers)
        {
            if (!await EnsureSignedInAsync(seat, stop))
            {
                continue;
            }

            if (await seat.User.MyAuctionsAsync(stop) is not { } mine)
            {
                // Unreadable this cycle: count what it had, so the marketplace doesn't overfill.
                live += seat.Active;
                continue;
            }

            // Drafts left by a listing cut short (a pause, a handover, a restart) would fill the seller's draft limit.
            foreach (var draft in mine.Where(a => a.Status == "draft"))
            {
                await seat.User.DeleteDraftAsync(draft.Id, Guid.NewGuid().ToString(), stop);
            }

            var active = mine.Where(a => a.Status == "active").ToList();
            seat.Active = seat.User.SignedIn ? active.Count : 0;
            live += seat.Active;
            foreach (var auction in active)
            {
                liveTitles.Add(auction.Title);
                if (_byTitle.TryGetValue(auction.Title, out var item))
                {
                    _own[auction.Id] = item;
                }
            }
        }

        // A failed read keeps the last good list, so valuations aren't forgotten and drawn again.
        if (await _browser.ActiveAuctionsAsync(stop) is { } market)
        {
            _market = market;
            Forget(market);
        }
        var catchUp = live < _options.TargetListings / 2;
        var wanted = Math.Min(_options.TargetListings - live, _options.MaxNewPerCycle);
        var failed = new HashSet<Seat>();
        for (var i = 0; i < wanted; i++)
        {
            // A seller whose listing failed sits out the rest of the cycle, so one stuck seller can't stop the others.
            var seat = _sellers.Where(s => s.User.SignedIn && s.RestingOn != today && !failed.Contains(s)).MinBy(s => s.Active);
            if (seat is null)
            {
                break;
            }

            var fresh = _catalogue.Where(c => !liveTitles.Contains(c.Title)).ToList();
            var item = fresh.Count > 0 ? fresh[planner.Pick(fresh.Count)] : _catalogue[planner.Pick(_catalogue.Count)];
            if (await ListAsync(seat, item, planner.NextListing(item, catchUp), today, stop))
            {
                seat.Active++;
                live++;
                liveTitles.Add(item.Title);
            }
            else
            {
                failed.Add(seat);
            }
        }

        Volatile.Write(ref _listingsLive, live);
    }

    // Draft, photo, publish: the steps a seller takes in the browser. A publish refused for the daily limit
    // rests the seller until the next UTC day and removes the draft it left.
    private async Task<bool> ListAsync(Seat seat, CatalogueItem item, Listing listing, DateOnly today, CancellationToken stop)
    {
        var user = seat.User;
        var now = _time.GetUtcNow();
        var created = await user.CreateDraftAsync(
            new
            {
                title = listing.Title,
                description = listing.Description,
                openingPriceCents = listing.OpeningCents,
                incrementCents = listing.IncrementCents,
                floorType = listing.FloorType,
                reserveCents = listing.ReserveCents,
                bidEntry = listing.BidEntry,
                startsAt = now.AddSeconds(-1),
                endsAt = now + listing.Duration,
            },
            Guid.NewGuid().ToString(), stop);
        if (created.Body is not { } draft || !draft.TryGetProperty("auctionId", out var auctionId))
        {
            Rest(seat, created, today);
            return false;
        }

        var id = auctionId.GetGuid();
        var photo = _photos.GetOrAdd(listing.Photo, name => Catalogue.Photo(_options.CatalogueDirectory, name));
        var upload = await user.UploadImageAsync(photo, stop);
        if (upload.Body is { } image && image.TryGetProperty("id", out var imageId))
        {
            await user.AttachImageAsync(id, imageId.GetGuid(), Guid.NewGuid().ToString(), stop);
        }

        var published = await user.PublishAsync(id, Guid.NewGuid().ToString(), stop);
        if (!published.Succeeded)
        {
            Rest(seat, published, today);
            await user.DeleteDraftAsync(id, Guid.NewGuid().ToString(), stop);
            return false;
        }

        _own[id] = item;
        return true;
    }

    private void Rest(Seat seat, CallResult refused, DateOnly today)
    {
        if (refused.Status == System.Net.HttpStatusCode.TooManyRequests)
        {
            seat.RestingOn = today;
            LogResting(_logger, seat.Email, refused.Code);
        }
    }

    private async Task BidLoopAsync(Seat seat, int index, CancellationToken stop)
    {
#pragma warning disable S2245 // seeded on purpose, as above
        var planner = new LivePlanner(new Random(unchecked((_simulation.Seed * 7919) + index + 1)));
#pragma warning restore S2245
        try
        {
            await _firstCycle.Task.WaitAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            var pause = _options.Cycle;
            await GuardedAsync(
                async () =>
                {
                    if (await EnsureSignedInAsync(seat, stop) && _market is { Count: > 0 } market)
                    {
                        var choice = Choose(market, planner);
                        await BidOnAsync(seat, index, choice.Id, planner, stop);
                        pause = planner.Think(choice.EndsAt - _time.GetUtcNow()) * _options.ThinkScale;
                    }
                },
                stop);
            await DelayAsync(pause, stop);
        }
    }

    // Half the time one of the five auctions ending soonest, so endings draw a crowd; otherwise any of them.
    private static ListedAuction Choose(IReadOnlyList<ListedAuction> market, LivePlanner planner)
    {
        if (planner.Pick(2) == 0)
        {
            var soonest = market.OrderBy(a => a.EndsAt).Take(5).ToList();
            return soonest[planner.Pick(soonest.Count)];
        }

        return market[planner.Pick(market.Count)];
    }

    private async Task BidOnAsync(Seat seat, int index, Guid auctionId, LivePlanner planner, CancellationToken stop)
    {
        if (await seat.User.AuctionAsync(auctionId, stop) is not { Status: "active" } auction)
        {
            return;
        }

        var valuation = _valuations.GetOrAdd((auction.Id, index), _ => planner.Valuation(auction.OpeningPriceCents, _own.GetValueOrDefault(auction.Id)));
        if (planner.NextBid(auction, valuation) is not { } amount)
        {
            return;
        }

        var key = Guid.NewGuid().ToString();
        var result = await seat.User.BidAsync(auction.Id, amount, key, stop);
        if (result.Retryable)
        {
            result = await seat.User.BidAsync(auction.Id, amount, key, stop);
        }

        if (result.Status == System.Net.HttpStatusCode.Created)
        {
            Interlocked.Increment(ref _bidsAccepted);
        }
    }

    private async Task VerifyLoopAsync(CancellationToken stop)
    {
        if (_simulation.VerifierConnectionString is not { Length: > 0 } connection)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connection);
        while (!stop.IsCancellationRequested)
        {
            await DelayAsync(_options.VerifyEvery, stop);
            await GuardedAsync(
                async () =>
                {
                    var report = await InvariantVerifier.VerifyAsync(dataSource, new VerifierScope(Since: _since), stop);
                    (_lastVerifiedAt, _lastVerifyPassed) = (_time.GetUtcNow(), report.Passed);
                    if (!report.Passed)
                    {
                        LogViolations(_logger, report.Violations.Count, string.Join("; ", report.Violations.Take(3).Select(v => $"{v.Rule}: {v.Detail}")));
                    }
                },
                stop);
        }
    }

    // A disabled account fails to sign in; it is tried again after a minute rather than every cycle. Sign-ins
    // beyond the minute's budget wait for a later cycle.
    private async Task<bool> EnsureSignedInAsync(Seat seat, CancellationToken stop)
    {
        if (seat.User.SignedIn)
        {
            return true;
        }

        var now = _time.GetUtcNow();
        if (seat.RetryAt > now || !TryTakeSignIn(now))
        {
            return false;
        }

        if (await seat.User.SignInAsync(seat.Email, _simulation.Password, stop))
        {
            seat.Failures = 0;
            return true;
        }

        seat.Failures++;
        seat.RetryAt = now + SignInBackoff(seat.Failures);
        return false;
    }

    /// <summary>
    /// How long to wait before trying a failing account's sign-in again (a disabled account, usually): a minute,
    /// doubling each time up to half an hour, so disabled accounts don't use up the sign-in budget or keep tripping
    /// the site's lockout. A reset re-enables them; the next try after it succeeds.
    /// </summary>
    public static TimeSpan SignInBackoff(int failures) =>
        TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, Math.Clamp(failures, 1, 6) - 1)));

    private bool TryTakeSignIn(DateTimeOffset now)
    {
        lock (_signInGate)
        {
            while (_signIns.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
            {
                _signIns.Dequeue();
            }

            if (_signIns.Count >= _options.SignInsPerMinute)
            {
                return false;
            }

            _signIns.Enqueue(now);
            return true;
        }
    }

    // Valuations of auctions no longer active are dropped, so memory stays bounded over days of running.
    private void Forget(IReadOnlyList<ListedAuction> market)
    {
        var active = market.Select(a => a.Id).ToHashSet();
        foreach (var key in _valuations.Keys.Where(k => !active.Contains(k.Auction)))
        {
            _valuations.TryRemove(key, out _);
        }

        foreach (var id in _own.Keys.Where(id => !active.Contains(id)))
        {
            _own.TryRemove(id, out _);
        }
    }

    private async Task GuardedAsync(Func<Task> step, CancellationToken stop)
    {
        try
        {
            await step();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping: the loop ends at its next check.
        }
        catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            // The loops must outlive any one failure: log it, and the next cycle tries again.
            LogStepFailed(_logger, e);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, _time, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [LoggerMessage(10, LogLevel.Information, "Live seller {Email} rests until tomorrow (UTC): {Code}")]
    private static partial void LogResting(ILogger logger, string email, string? code);

    [LoggerMessage(11, LogLevel.Warning, "Live marketplace step failed; the next cycle tries again")]
    private static partial void LogStepFailed(ILogger logger, Exception exception);

    [LoggerMessage(12, LogLevel.Error, "The verifier found {Count} violation(s) in the live marketplace: {Sample}")]
    private static partial void LogViolations(ILogger logger, int count, string sample);

    private sealed class Seat(string email, Uri site)
    {
        public string Email { get; } = email;

        public VirtualUser User { get; } = new(site);

        public int Active { get; set; }

        public DateOnly? RestingOn { get; set; }

        public DateTimeOffset RetryAt { get; set; }

        public int Failures { get; set; }
    }
}
