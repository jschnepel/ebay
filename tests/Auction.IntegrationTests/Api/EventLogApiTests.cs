using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// The admin event log (ADM-03): account, admin, listing and bid events merged newest first, each naming the people
/// and items involved, filtered by time, category, search and user. Tests read only events they made.
/// </summary>
public sealed class EventLogApiTests(MigratedDatabase db)
{
    [Fact]
    public async Task Every_category_appears_with_the_people_and_items_named()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Log admin", role: "admin");
        var seller = await api.SignUpAsync("Log seller");
        var buyer = await api.SignUpAsync("Log buyer");
        var title = $"Log lot {Guid.NewGuid():N}"[..16];
        var listed = await seller.PublishedAsync(ApiCalls.Terms(title: title));
        using (var bid = await buyer.Http.BidAsync(listed, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var unknown = $"{Guid.NewGuid():N}@example.test";
        (_, var failed) = await api.SignInAsync(unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

        var sellerCancels = await seller.PublishedAsync(ApiCalls.Terms(title: title + " b"));
        using (var cancel = await seller.Http.PostCommandAsync($"/api/v1/auctions/{sellerCancels}/cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var adminCancels = await seller.PublishedAsync(ApiCalls.Terms(title: title + " c"));
        using (var cancel = await admin.Http.PostCommandAsync($"/api/v1/admin/auctions/{adminCancels}/cancel", new { reason = "Counterfeit" }))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        using (var disable = await admin.Http.PostAsJsonAsync($"/api/v1/admin/users/{buyer.Id}/disable", new { reason = "Spam bids" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        }

        var events = await AllAsync(admin.Http, "");

        var signUp = Single(events, e => e.Type == "sign_up" && e.ActorId == seller.Id);
        Assert.Equal(("account", "info", "Log seller", seller.Email), (signUp.Category, signUp.Level, signUp.ActorName, signUp.ActorEmail));

        var failedSignIn = Single(events, e => e.Type == "sign_in_failed" && e.TargetEmail == unknown);
        Assert.Equal(("warning", "email", (string?)null, "unknown_email"), (failedSignIn.Level, failedSignIn.TargetKind, failedSignIn.ActorName, failedSignIn.Detail["cause"]));

        var published = Single(events, e => e.Type == "published" && e.TargetId == listed);
        Assert.Equal(("listing", "Log seller", title), (published.Category, published.ActorName, published.TargetName));

        var placed = Single(events, e => e.Category == "bid" && e.TargetId == listed);
        Assert.Equal(("Log buyer", buyer.Email, title), (placed.ActorName, placed.ActorEmail, placed.TargetName));

        var disabled = Single(events, e => e.Type == "disable" && e.TargetId == buyer.Id);
        Assert.Equal(("admin", "warning", "Log admin", "Log buyer", buyer.Email, "Spam bids"),
            (disabled.Category, disabled.Level, disabled.ActorName, disabled.TargetName, disabled.TargetEmail, disabled.Detail["reason"]));

        var cancelled = Single(events, e => e.Type == "cancelled" && e.TargetId == sellerCancels);
        Assert.Equal(("listing", "Log seller"), (cancelled.Category, cancelled.ActorName));

        // An admin's cancel is an admin action, once; it isn't also a listing cancelled by its seller.
        Assert.Single(events, e => e.Type == "cancel" && e.TargetId == adminCancels && e.Category == "admin");
        Assert.DoesNotContain(events, e => e.Type == "cancelled" && e.TargetId == adminCancels);
    }

    [Fact]
    public async Task A_closed_listing_reports_its_outcome()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Close admin", role: "admin");
        var seller = await api.SignUpAsync("Close seller");
        var winner = await api.SignUpAsync("Close winner");
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: "Closing lot"));
        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "UPDATE auctions SET status = 'closed', outcome = 'sold', current_price_cents = 4200, leader_id = $2, accepted_bid_count = 1, closed_at = clock_timestamp() WHERE id = $1",
            id,
            winner.Id);

        var closed = Single(await AllAsync(admin.Http, $"&q={id}"), e => e.Type == "closed");

        Assert.Equal(("listing", (string?)null, "Closing lot", "sold", "4200"),
            (closed.Category, closed.ActorName, closed.TargetName, closed.Detail["outcome"], closed.Detail["finalPriceCents"]));
    }

    [Fact]
    public async Task Category_search_and_user_filters_narrow_the_log()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Filter admin", role: "admin");
        var sellerName = $"Filterseller {Guid.NewGuid():N}"[..20];
        var seller = await api.SignUpAsync(sellerName);
        var buyer = await api.SignUpAsync("Filter buyer");
        var other = await api.SignUpAsync("Filter other");
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: "Filtered lot"));
        using (var bid = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        using (var bid = await other.Http.BidAsync(id, 1800))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var bids = await AllAsync(admin.Http, "&category=bid");
        Assert.NotEmpty(bids);
        Assert.All(bids, e => Assert.Equal("bid", e.Category));

        var byName = await AllAsync(admin.Http, $"&q={Uri.EscapeDataString(sellerName[4..])}");
        Assert.Contains(byName, e => e.Type == "published" && e.TargetId == id);
        var byEmail = await AllAsync(admin.Http, $"&q={Uri.EscapeDataString(seller.Email)}");
        Assert.Contains(byEmail, e => e.Type == "sign_up" && e.ActorId == seller.Id);

        // A user's events: their own actions and their listing's, not other people's bids on it.
        var sellers = await AllAsync(admin.Http, $"&user={seller.Id}");
        Assert.Contains(sellers, e => e.Type == "sign_up");
        Assert.Contains(sellers, e => e.Type == "published" && e.TargetId == id);
        Assert.DoesNotContain(sellers, e => e.Category == "bid");

        var buyers = await AllAsync(admin.Http, $"&user={buyer.Id}");
        Assert.Contains(buyers, e => e.Category == "bid" && e.ActorId == buyer.Id);
        Assert.DoesNotContain(buyers, e => e.Category == "bid" && e.ActorId == other.Id);
    }

    [Fact]
    public async Task Events_with_the_same_time_are_paged_without_loss_or_repeat()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Paging admin", role: "admin");
        var user = Guid.NewGuid();
        await db.ExecuteAsync(
            MigratedDatabase.Migrator, "INSERT INTO users (id, email, normalized_email, display_name) VALUES ($1, $2, upper($2), 'Same time')", user, $"{user:N}@example.test");
        var at = await db.ScalarAsync<DateTime>(MigratedDatabase.Migrator, "SELECT clock_timestamp() - interval '30 minutes'");
        foreach (var type in new[] { "sign_in", "sign_out", "sign_in" })
        {
            await db.ExecuteAsync(
                MigratedDatabase.Migrator, "INSERT INTO account_events (at, type, user_id, session_id) VALUES ($1, $2, $3, gen_random_uuid())", at, type, user);
        }

        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "INSERT INTO admin_audit (admin_id, action, target_type, target_id, reason, at) VALUES ($1, 'disable', 'user', $2, 'same time', $3)",
            admin.Id,
            user,
            at);

        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = await admin.Http.GetJsonAsync($"/api/v1/admin/events?user={user}&pageSize=2{(cursor is null ? "" : $"&cursor={cursor}")}");
            keys.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("key").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(4, keys.Count);
        Assert.Equal(4, keys.Distinct().Count());
    }

    [Fact]
    public async Task A_private_proxy_amount_stays_private_in_the_log()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Private admin", role: "admin");
        var auction = await db.InsertActiveAuctionAsync();
        await db.InsertBidAsync(auction, null);

        var bid = Single(await AllAsync(admin.Http, $"&q={auction}&category=bid"), e => e.TargetId == auction);

        Assert.True(bid.Detail.ContainsKey("amountCents"));
        Assert.Null(bid.Detail["amountCents"]);
    }

    [Fact]
    public async Task The_histogram_counts_each_category_per_bucket()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Chart admin", role: "admin");
        var seller = await api.SignUpAsync("Chart seller");
        var buyer = await api.SignUpAsync("Chart buyer");
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: "Charted lot", bidEntry: "fixed_increment"));
        foreach (var amount in new long[] { 1000, 1100 })
        {
            using var bid = await buyer.Http.BidAsync(id, amount);
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var to = DateTimeOffset.UtcNow.AddSeconds(1);
        var chart = await admin.Http.GetJsonAsync(
            $"/api/v1/admin/events/histogram?from={Uri.EscapeDataString(to.AddMinutes(-15).ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}&user={buyer.Id}");

        Assert.Equal(30, chart.GetProperty("bucketSeconds").GetInt32());
        var buckets = chart.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(30, buckets.Count);
        long Sum(string category) => buckets.Sum(b => b.GetProperty("counts").GetProperty(category).GetInt64());
        Assert.Equal((2L, 1L, 0L, 0L), (Sum("bid"), Sum("account"), Sum("listing"), Sum("admin")));

        // It all happened in the last minute, so it is counted in the last two half-minute buckets and nowhere else.
        static long Total(JsonElement bucket) => bucket.GetProperty("counts").EnumerateObject().Sum(c => c.Value.GetInt64());
        Assert.Equal(0, buckets.Take(28).Sum(Total));
        Assert.Equal(3, buckets.Skip(28).Sum(Total));
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(60, 60)]
    [InlineData(24 * 60, 1800)]
    [InlineData(7 * 24 * 60, 21600)]
    public async Task The_bucket_size_follows_the_range(int minutes, int seconds)
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Bucket admin", role: "admin");
        var to = DateTimeOffset.UtcNow;

        var chart = await admin.Http.GetJsonAsync(
            $"/api/v1/admin/events/histogram?from={Uri.EscapeDataString(to.AddMinutes(-minutes).ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}");

        Assert.Equal(seconds, chart.GetProperty("bucketSeconds").GetInt32());
    }

    [Fact]
    public async Task A_search_takes_percent_and_underscore_literally()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Wildcard admin", role: "admin");
        var marker = Guid.NewGuid().ToString("N")[..8];
        var plain = await api.SignUpAsync($"Plain {marker}");
        var percent = await api.SignUpAsync($"100% {marker}");

        var hits = await AllAsync(admin.Http, $"&category=account&q={Uri.EscapeDataString($"0% {marker}")}");
        var all = await AllAsync(admin.Http, $"&category=account&q={Uri.EscapeDataString($"_ {marker}")}");

        Assert.Contains(hits, e => e.ActorId == percent.Id);
        Assert.DoesNotContain(hits, e => e.ActorId == plain.Id);
        // "_" is a character to find, not "any one character": no name here has an underscore before the marker.
        Assert.Empty(all);
    }

    [Fact]
    public async Task The_range_is_checked_and_the_log_is_for_admins_only()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Range admin", role: "admin");
        var user = await api.SignUpAsync("Not an admin");
        var now = DateTimeOffset.UtcNow;

        using (var backwards = await admin.Http.GetAsync(Range(now, now.AddHours(-1)), TestContext.Current.CancellationToken))
        {
            await backwards.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_range");
        }

        using (var tooLong = await admin.Http.GetAsync(Range(now.AddDays(-8), now), TestContext.Current.CancellationToken))
        {
            await tooLong.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_range");
        }

        using (var unknown = await admin.Http.GetAsync("/api/v1/admin/events?category=weather", TestContext.Current.CancellationToken))
        {
            await unknown.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_category");
        }

        // Dates at either end of what a date can hold are refused, for the list and the chart alike.
        foreach (var route in new[] { "/api/v1/admin/events", "/api/v1/admin/events/histogram" })
        {
            foreach (var range in new[] { "to=0001-01-01T00:00:00Z", "from=9999-12-31T00:00:00Z&to=9999-12-31T23:59:59Z" })
            {
                using var extreme = await admin.Http.GetAsync($"{route}?{range}", TestContext.Current.CancellationToken);
                await extreme.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_range");
            }
        }

        using (var longSearch = await admin.Http.GetAsync($"/api/v1/admin/events?q={new string('a', 101)}", TestContext.Current.CancellationToken))
        {
            await longSearch.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_search");
        }

        // The same routes answer an admin, so the 404s below are the admin check and not a missing route.
        foreach (var route in new[] { "/api/v1/admin/events", "/api/v1/admin/events/histogram" })
        {
            Assert.Equal(HttpStatusCode.OK, await admin.Http.StatusOfGetAsync(route));
            Assert.Equal(HttpStatusCode.NotFound, await user.Http.StatusOfGetAsync(route));
            Assert.Equal(HttpStatusCode.NotFound, await api.Anonymous().StatusOfGetAsync(route));
        }
    }

    private static string Range(DateTimeOffset from, DateTimeOffset to) =>
        $"/api/v1/admin/events?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";

    private static Event Single(IEnumerable<Event> events, Func<Event, bool> match) => Assert.Single(events, e => match(e));

    // Every page of the last ten minutes, following the cursor; the test host's pages are small.
    private static async Task<List<Event>> AllAsync(HttpClient admin, string filters)
    {
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O", CultureInfo.InvariantCulture));
        var events = new List<Event>();
        string? cursor = null;
        do
        {
            var page = await admin.GetJsonAsync($"/api/v1/admin/events?from={from}{filters}{(cursor is null ? "" : $"&cursor={cursor}")}");
            events.AddRange(page.GetProperty("items").EnumerateArray().Select(Event.From));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return events;
    }

    private sealed record Event(
        string Key, string Category, string Type, string Level,
        Guid? ActorId, string? ActorName, string? ActorEmail,
        string? TargetKind, Guid? TargetId, string? TargetName, string? TargetEmail,
        Dictionary<string, string?> Detail)
    {
        public static Event From(JsonElement e)
        {
            var actor = e.GetProperty("actor");
            var target = e.GetProperty("target");
            return new Event(
                e.GetProperty("key").GetString()!, e.GetProperty("category").GetString()!, e.GetProperty("type").GetString()!, e.GetProperty("level").GetString()!,
                Id(actor), Text(actor, "name"), Text(actor, "email"),
                Text(target, "kind"), Id(target), Text(target, "name"), Text(target, "email"),
                e.GetProperty("detail").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString()));
        }

        private static Guid? Id(JsonElement party) =>
            party.ValueKind == JsonValueKind.Object && party.GetProperty("id").ValueKind == JsonValueKind.String ? party.GetProperty("id").GetGuid() : null;

        private static string? Text(JsonElement party, string name) =>
            party.ValueKind == JsonValueKind.Object ? party.GetProperty(name).GetString() : null;
    }
}
