using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>The admin area over HTTP (UI-04, ADM-01, §7): moderation through the engine, audited, hidden from everyone else.</summary>
public sealed class AdminApiTests(MigratedDatabase db)
{
    private const string AdminPassword = "admin-password-for-tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/v1/admin/activity")]
    [InlineData("/api/v1/admin/health")]
    [InlineData("/api/v1/admin/kpis")]
    [InlineData("/api/v1/admin/auctions")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/admin/simulator")]
    [InlineData("/api/v1/admin/audit")]
    [InlineData("/api/v1/admin/demo")]
    public async Task Admin_routes_are_not_found_for_anyone_but_an_admin(string route)
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync();
        var admin = await AdminAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, await api.Anonymous().StatusOfGetAsync(route));
        Assert.Equal(HttpStatusCode.NotFound, await user.Http.StatusOfGetAsync(route));
        Assert.Equal(HttpStatusCode.OK, await admin.StatusOfGetAsync(route));
    }

    [Fact]
    public async Task Suspend_stops_bids_resume_restores_them_and_cancel_keeps_the_history_each_audited_with_its_reason()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        using (var first = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using (var suspend = await ModerateAsync(admin, id, "suspend", "Checking the listing"))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        using (var refused = await buyer.Http.BidAsync(id, 2500))
        {
            await refused.AssertProblemAsync(HttpStatusCode.Conflict, "not_active");
        }

        using (var resume = await ModerateAsync(admin, id, "resume", "Listing is fine"))
        {
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        }

        using (var accepted = await buyer.Http.BidAsync(id, 2500))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode); // control
        }

        using (var cancel = await ModerateAsync(admin, id, "cancel", "Counterfeit item"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        Assert.Equal("cancelled", (await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("status").GetString());
        Assert.NotEmpty((await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}/bids")).GetProperty("items").EnumerateArray());
        Assert.Equal(
            "suspend:Checking the listing,resume:Listing is fine,cancel:Counterfeit item",
            await db.ScalarAsync<string>(
                MigratedDatabase.Migrator,
                "SELECT string_agg(action || ':' || reason, ',' ORDER BY id) FROM admin_audit WHERE target_id = $1 AND target_type = 'auction'",
                id));
    }

    [Fact]
    public async Task Moderation_needs_a_reason_a_valid_state_and_a_published_auction()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);
        var seller = await api.SignUpAsync();
        var scheduled = await seller.PublishedAsync(ApiCalls.Terms(startIn: TimeSpan.FromHours(1)));
        var draft = await seller.Http.CreateDraftAsync();
        var active = await seller.PublishedAsync();

        using var noReason = await ModerateAsync(admin, active, "suspend", "  ");
        using var notActive = await ModerateAsync(admin, scheduled, "suspend", "Reason");
        using var onDraft = await ModerateAsync(admin, draft, "cancel", "Reason");
        using var resumeActive = await ModerateAsync(admin, active, "resume", "Reason");

        await noReason.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await notActive.AssertProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        await onDraft.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await resumeActive.AssertProblemAsync(HttpStatusCode.Conflict, "invalid_state");
    }

    [Fact]
    public async Task A_disabled_user_is_signed_out_on_the_next_request_and_others_are_unaffected()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);
        var target = await api.SignUpAsync();
        var bystander = await api.SignUpAsync();
        var adminId = (await admin.GetJsonAsync("/api/v1/account/me")).GetProperty("id").GetGuid();

        using (var disable = await admin.PostAsJsonAsync($"/api/v1/admin/users/{target.Id}/disable", new { reason = "Abuse report" }, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await target.Http.StatusOfGetAsync("/api/v1/account/me"));
        Assert.Equal(HttpStatusCode.OK, await bystander.Http.StatusOfGetAsync("/api/v1/account/me")); // control
        var (_, signIn) = await api.SignInAsync(target.Email);
        await signIn.AssertProblemAsync(HttpStatusCode.Unauthorized, "sign_in_failed");
        using var self = await admin.PostAsJsonAsync($"/api/v1/admin/users/{adminId}/disable", new { reason = "Oops" }, Ct);
        await self.AssertProblemAsync(HttpStatusCode.Conflict, "own_account");

        using (var enable = await admin.PostAsJsonAsync($"/api/v1/admin/users/{target.Id}/enable", new { reason = "Resolved" }, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        }

        var (_, again) = await api.SignInAsync(target.Email);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM admin_audit WHERE target_id = $1 AND target_type = 'user'", target.Id));
    }

    [Fact]
    public async Task The_moderation_lists_search_by_seller_name_status_and_id_and_never_show_drafts()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);
        var sellerName = $"Seller {Guid.NewGuid():N}"[..20];
        var seller = await api.SignUpAsync(sellerName);
        var active = await seller.PublishedAsync();
        var scheduled = await seller.PublishedAsync(ApiCalls.Terms(startIn: TimeSpan.FromHours(1)));
        var draft = await seller.Http.CreateDraftAsync();

        var bySeller = await IdsAsync(admin, $"/api/v1/admin/auctions?q={Uri.EscapeDataString(sellerName)}&pageSize=5");
        var activeOnly = await IdsAsync(admin, $"/api/v1/admin/auctions?q={seller.Id}&status=active");
        var users = await IdsAsync(admin, $"/api/v1/admin/users?q={Uri.EscapeDataString(sellerName)}");
        var userById = await IdsAsync(admin, $"/api/v1/admin/users?q={seller.Id}");

        Assert.Equal(new[] { active, scheduled }.Order(), bySeller.Order());
        Assert.DoesNotContain(draft, bySeller);
        Assert.Equal([active], activeOnly);
        Assert.Equal([seller.Id], users);
        Assert.Equal([seller.Id], userById);
    }

    [Fact]
    public async Task Health_reports_every_component_and_the_live_update_backlog()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);

        var health = await admin.GetJsonAsync("/api/v1/admin/health");

        var states = health.GetProperty("components").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("state").GetString());
        Assert.Equal("ok", states["API"]);
        Assert.Equal("ok", states["Engine"]);
        Assert.Equal("ok", states["PostgreSQL"]);
        Assert.Equal("ok", states["Redis"]);
        Assert.True(health.GetProperty("outboxBacklog").GetInt64() >= 0);
    }

    [Fact]
    public async Task An_owner_deletes_a_draft_but_nobody_deletes_someone_elses_or_a_published_one()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var other = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var published = await seller.PublishedAsync();

        using var byOther = await other.Http.CommandAsync(HttpMethod.Delete, $"/api/v1/auctions/{draft}");
        using var onPublished = await seller.Http.CommandAsync(HttpMethod.Delete, $"/api/v1/auctions/{published}");
        using var byOwner = await seller.Http.CommandAsync(HttpMethod.Delete, $"/api/v1/auctions/{draft}");

        await byOther.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await onPublished.AssertProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await seller.Http.StatusOfGetAsync($"/api/v1/auctions/{draft}"));
    }

    [Fact]
    public async Task The_audit_log_pages_through_an_admins_actions_newest_first_with_who_what_target_and_reason()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);
        var adminId = (await admin.GetJsonAsync("/api/v1/account/me")).GetProperty("id").GetGuid();
        var id = await (await api.SignUpAsync()).PublishedAsync();
        var user = await api.SignUpAsync();
        foreach (var (action, reason) in new[] { ("suspend", "Audit one"), ("resume", "Audit two") })
        {
            using var moderated = await ModerateAsync(admin, id, action, reason);
            Assert.Equal(HttpStatusCode.OK, moderated.StatusCode);
        }

        using (var disabled = await admin.PostCommandAsync($"/api/v1/admin/users/{user.Id}/disable", new { reason = "Audit three" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        }

        var first = await admin.GetJsonAsync($"/api/v1/admin/audit?q={adminId}&pageSize=2");
        var second = await admin.GetJsonAsync($"/api/v1/admin/audit?q={adminId}&pageSize=2&cursor={first.GetProperty("nextCursor").GetString()}");
        var entries = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToList();

        Assert.Equal(
            [("disable", "user", user.Id, "Audit three"), ("resume", "auction", id, "Audit two"), ("suspend", "auction", id, "Audit one")],
            entries.Select(e => (e.GetProperty("action").GetString(), e.GetProperty("targetType").GetString(), e.GetProperty("targetId").GetGuid(), e.GetProperty("reason").GetString())));
        Assert.All(entries, e => Assert.Equal(("admin account", adminId), (e.GetProperty("adminName").GetString(), e.GetProperty("adminId").GetGuid())));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.True(entries[0].GetProperty("at").GetDateTimeOffset() >= entries[2].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Without_a_simulator_the_panel_says_unavailable_and_a_start_or_stop_is_503()
    {
        var api = await db.ApiAsync();
        var admin = await AdminAsync(api);

        var view = await admin.GetJsonAsync("/api/v1/admin/simulator");
        using var start = await admin.PostAsJsonAsync("/api/v1/admin/simulator/runs", new { profile = "smoke" }, TestContext.Current.CancellationToken);
        using var stop = await admin.DeleteAsync(new Uri("/api/v1/admin/simulator/runs/current", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.False(view.GetProperty("available").GetBoolean());
        await start.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "simulator_unavailable");
        await stop.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "simulator_unavailable");
    }

    [Fact]
    public async Task An_admin_asks_for_a_demo_reset_once_and_a_second_request_waits_for_the_first()
    {
        await db.ExecuteAsync(MigratedDatabase.Migrator, "DELETE FROM demo_resets");
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Visiting admin", role: "admin");
        var user = await api.SignUpAsync();

        using var notAdmin = await user.Http.PostAsJsonAsync("/api/v1/admin/demo/reset", new { }, Ct);
        using var first = await admin.Http.PostAsJsonAsync("/api/v1/admin/demo/reset", new { }, Ct);
        using var second = await admin.Http.PostAsJsonAsync("/api/v1/admin/demo/reset", new { }, Ct);
        var state = await admin.Http.GetJsonAsync("/api/v1/admin/demo");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Accepted), (notAdmin.StatusCode, first.StatusCode));
        await second.AssertProblemAsync(HttpStatusCode.Conflict, "reset_pending");
        Assert.Equal("Visiting admin", await db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT requested_by FROM demo_resets WHERE completed_at IS NULL"));
        Assert.True(state.GetProperty("resetPending").GetBoolean());
        var next = state.GetProperty("nextScheduledAt").GetDateTimeOffset();
        Assert.InRange(next, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(TimeSpan.FromHours(9), next.UtcDateTime.TimeOfDay);
        await db.ExecuteAsync(MigratedDatabase.Migrator, "DELETE FROM demo_resets");
    }

    [Fact]
    public async Task Outside_demo_mode_there_is_no_demo_reset()
    {
        var api = await db.ApiAsync(demoMode: false);
        var admin = await AdminAsync(api);

        using var reset = await admin.PostAsJsonAsync("/api/v1/admin/demo/reset", new { }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
    }

    private static Task<HttpResponseMessage> ModerateAsync(HttpClient admin, Guid id, string action, string reason) =>
        admin.PostCommandAsync($"/api/v1/admin/auctions/{id}/{action}", new { reason });

    private static async Task<List<Guid>> IdsAsync(HttpClient http, string url) =>
        [.. (await http.GetJsonAsync(url)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private async Task<HttpClient> AdminAsync(ApiHost api)
    {
        var email = await db.InsertPasswordAccountAsync("admin", AdminPassword);
        var (http, response) = await api.SignInAsync(email, AdminPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Dispose();
        return http;
    }
}
