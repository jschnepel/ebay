using System.Net;
using System.Net.Http.Json;
using Auction.Api.Images;
using Auction.IntegrationTests.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Auction.IntegrationTests.Api;

/// <summary>UI-05: uploads checked by signature and size, owner-only links under the auction lock, frozen at publish, served safely.</summary>
public sealed class ImageApiTests(MigratedDatabase db)
{
    private const int MiB = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Truncated_and_excessive_dimension_images_are_refused_before_storage()
    {
        var seller = await (await db.ApiAsync()).SignUpAsync();
        var png = TestImages.Png();
        var large = (byte[])png.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(large.AsSpan(16), 100_000);

        foreach (var data in new[] { png[..24], large })
        {
            using var response = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(data, "image/png"), Ct);
            await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        }

        Assert.Equal(0, await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM image_blobs WHERE owner_id = $1", seller.Id));
    }

    [Fact]
    public async Task A_jpeg_a_png_and_a_webp_attach_with_a_cover_and_after_publish_anyone_sees_them()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var jpeg = await UploadAsync(seller, TestImages.Jpeg(), "image/jpeg");
        var png = await UploadAsync(seller, TestImages.Png(), "image/png");
        var webp = await UploadAsync(seller, TestImages.WebP(), "image/webp");
        foreach (var image in new[] { jpeg, png, webp })
        {
            using var attach = await AttachAsync(seller, draft, image);
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        }

        using (var cover = await seller.Http.PostCommandAsync($"/api/v1/auctions/{draft}/images/{png}/cover"))
        {
            Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        }

        using (var publish = await seller.Http.PostCommandAsync($"/api/v1/auctions/{draft}/publish"))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        var anonymous = api.Anonymous();
        var detail = await anonymous.GetJsonAsync($"/api/v1/auctions/{draft}");
        Assert.Equal([jpeg, png, webp], detail.GetProperty("imageIds").EnumerateArray().Select(i => i.GetGuid()));
        Assert.Equal(png, detail.GetProperty("coverImageId").GetGuid());
        var listed = (await seller.Http.GetJsonAsync("/api/v1/me/auctions?pageSize=5")).GetProperty("items").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == draft);
        Assert.Equal(png, listed.GetProperty("coverImageId").GetGuid());

        using var served = await anonymous.GetAsync(new Uri($"/api/v1/images/{png}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("immutable", served.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        using var servedWebp = await anonymous.GetAsync(new Uri($"/api/v1/images/{webp}", UriKind.Relative), Ct);
        Assert.Equal("image/webp", servedWebp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Uploads_are_checked_by_their_bytes_and_size_and_a_retry_returns_the_same_image()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var image = TestImages.Png();

        using var first = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(image, "image/png"), Ct);
        using var retry = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(image, "image/png"), Ct);
        using var oversized = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(5 * MiB + MiB / 2), "image/png"), Ct);
        using var fake = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.NotAnImage(), "image/png"), Ct);
        using var mismatched = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Jpeg(), "image/png"), Ct);
        using var tooLarge = await seller.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(7 * MiB), "image/png"), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await first.JsonAsync()).GetProperty("id").GetGuid(), (await retry.JsonAsync()).GetProperty("id").GetGuid());
        await oversized.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await fake.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await mismatched.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await tooLarge.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [Fact]
    public async Task A_legacy_blob_keeps_its_id_and_only_decodable_pixels_are_served()
    {
        var seller = await (await db.ApiAsync()).SignUpAsync();
        var id = Guid.NewGuid();
        var original = TestImages.Png();
        byte[] legacy = [.. original, .. "legacy-trailer"u8.ToArray()];
        const string insert = """
            INSERT INTO image_blobs (id, owner_id, sha256, content_type, size_bytes, width, height, data)
            VALUES ($1, $2, $3, 'image/png', $4, 4, 4, $5)
            """;
        await db.ExecuteAsync(MigratedDatabase.Migrator, insert, id, seller.Id, System.Security.Cryptography.SHA256.HashData(legacy), legacy.Length, legacy);
        using var served = await seller.Http.GetAsync(new Uri($"/api/v1/images/{id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var rendition = await served.Content.ReadAsByteArrayAsync(Ct);
        Assert.False(rendition.AsSpan().EndsWith("legacy-trailer"u8));

        var invalidId = Guid.NewGuid();
        var truncated = original[..24];
        await db.ExecuteAsync(MigratedDatabase.Migrator, insert, invalidId, seller.Id, System.Security.Cryptography.SHA256.HashData(truncated), truncated.Length, truncated);
        Assert.Equal(HttpStatusCode.NotFound, await seller.Http.StatusOfGetAsync($"/api/v1/images/{invalidId}"));
    }

    [Fact]
    public async Task Only_the_owner_links_their_own_images_to_their_own_draft()
    {
        var api = await db.ApiAsync();
        var a = await api.SignUpAsync();
        var b = await api.SignUpAsync();
        var aBlob = await UploadAsync(a, TestImages.Png(), "image/png");
        var bBlob = await UploadAsync(b, TestImages.Png(), "image/png");
        var aDraft = await a.Http.CreateDraftAsync();
        var bDraft = await b.Http.CreateDraftAsync();

        using var stolen = await AttachAsync(b, bDraft, aBlob);
        using var intruder = await AttachAsync(b, aDraft, bBlob);
        using var own = await AttachAsync(b, bDraft, bBlob);
        using var removeOthers = await a.Http.CommandAsync(HttpMethod.Delete, $"/api/v1/auctions/{bDraft}/images/{bBlob}");

        await stolen.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await intruder.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode); // control
        await removeOthers.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Five_racing_attaches_land_exactly_four_and_images_freeze_at_publish()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var blobs = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            blobs.Add(await UploadAsync(seller, TestImages.Png(), "image/png"));
        }

        var raced = await Task.WhenAll(blobs.Take(5).Select(async blob =>
        {
            using var response = await AttachAsync(seller, draft, blob);
            return response.StatusCode;
        }));

        Assert.Equal(4, raced.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, raced.Count(s => s == HttpStatusCode.Conflict));
        var attached = (await seller.Http.GetJsonAsync($"/api/v1/auctions/{draft}")).GetProperty("imageIds").EnumerateArray().Select(i => i.GetGuid()).ToList();
        Assert.Equal(4, attached.Count);

        using (var publish = await seller.Http.PostCommandAsync($"/api/v1/auctions/{draft}/publish"))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        using var remove = await seller.Http.CommandAsync(HttpMethod.Delete, $"/api/v1/auctions/{draft}/images/{attached[0]}");
        await remove.AssertProblemAsync(HttpStatusCode.Conflict, "invalid_state");
    }

    [Fact]
    public async Task A_drafts_images_are_only_for_its_seller_and_never_cached()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var other = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var blob = await UploadAsync(seller, TestImages.Png(), "image/png");
        using (var attach = await AttachAsync(seller, draft, blob))
        {
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        }

        using var own = await seller.Http.GetAsync(new Uri($"/api/v1/images/{blob}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.True(own.Headers.CacheControl is { Private: true, NoStore: true }, own.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, await api.Anonymous().StatusOfGetAsync($"/api/v1/images/{blob}"));
        Assert.Equal(HttpStatusCode.NotFound, await other.Http.StatusOfGetAsync($"/api/v1/images/{blob}"));
    }

    [Fact]
    public async Task Counted_limits_hold_under_concurrency_and_answer_429_409_or_507()
    {
        await using var limited = await db.ApiWithAsync(new Dictionary<string, string>
        {
            ["Images:UploadsPerHour"] = "3",
            ["Images:UserQuotaBytes"] = "5000",
            ["Images:TotalQuotaBytes"] = "1000000000000",
        });
        var uploader = await limited.SignUpAsync();
        var hoarder = await limited.SignUpAsync();

        var rates = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var upload = await uploader.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(), "image/png"), Ct);
            rates.Add(upload.StatusCode);
        }

        // Each upload is about 2.2 kB: of five racing uploads against a 5,000-byte quota, exactly two fit
        // (a refused upload doesn't count toward the hourly limit).
        var racing = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var upload = await hoarder.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(1500), "image/png"), Ct);
            return upload.StatusCode;
        }));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], rates);
        Assert.Equal(2, racing.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(3, racing.Count(s => s == HttpStatusCode.InsufficientStorage));
    }

    [Fact]
    public async Task Uploads_from_different_users_racing_past_the_total_cap_stop_exactly_at_it()
    {
        await using var small = await db.ApiWithAsync(new Dictionary<string, string> { ["Images:TotalQuotaBytes"] = "0" });
        var global = await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT bytes_used FROM global_quota WHERE id = 1");
        await small.DisposeAsync();

        // Room for exactly two more 2.2 kB images in the whole demo, whatever is stored already.
        await using var capped = await db.ApiWithAsync(new Dictionary<string, string> { ["Images:TotalQuotaBytes"] = (global + 5000).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var users = new[] { await capped.SignUpAsync(), await capped.SignUpAsync(), await capped.SignUpAsync() };

        var racing = await Task.WhenAll(users.Select(async user =>
        {
            using var upload = await user.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(1500), "image/png"), Ct);
            return upload.StatusCode;
        }));

        Assert.Equal(2, racing.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, racing.Count(s => s == HttpStatusCode.InsufficientStorage));
    }

    [Fact]
    public async Task Racing_draft_creates_stop_at_the_open_draft_cap_and_the_daily_publish_cap_answers_429()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();

        var creates = await Task.WhenAll(Enumerable.Range(0, 11).Select(async _ =>
        {
            using var response = await seller.Http.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms());
            return response.StatusCode;
        }));

        Assert.Equal(10, creates.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, creates.Count(s => s == HttpStatusCode.Conflict));

        var publisher = await api.SignUpAsync();
        for (var i = 0; i < 20; i++)
        {
            await publisher.PublishedAsync();
        }

        var lastDraft = await publisher.Http.CreateDraftAsync();
        using var overCap = await publisher.Http.PostCommandAsync($"/api/v1/auctions/{lastDraft}/publish");
        await overCap.AssertProblemAsync(HttpStatusCode.TooManyRequests, "publish_limit");
    }

    [Fact]
    public async Task The_sweep_removes_unattached_images_past_their_lifetime_and_returns_their_quota()
    {
        await using var quick = await db.ApiWithAsync(new Dictionary<string, string> { ["Images:UnattachedLifetime"] = "00:00:01" });
        var seller = await quick.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var kept = await UploadAsync(seller, TestImages.Png(), "image/png");
        var dropped = await UploadAsync(seller, TestImages.Png(), "image/png");
        using (var attach = await AttachAsync(seller, draft, kept))
        {
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        await quick.Services.GetRequiredService<ImageStore>().SweepAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, await seller.Http.StatusOfGetAsync($"/api/v1/images/{kept}"));
        Assert.Equal(HttpStatusCode.NotFound, await seller.Http.StatusOfGetAsync($"/api/v1/images/{dropped}"));
        Assert.Equal(await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT size_bytes::bigint FROM image_blobs WHERE id = $1", kept),
            await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT bytes_used FROM upload_quotas WHERE user_id = $1", seller.Id));
    }

    [Fact]
    public async Task The_database_refuses_deleting_an_attached_image_and_changing_links_after_publish()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var blob = await UploadAsync(seller, TestImages.Png(), "image/png");
        using (var attach = await AttachAsync(seller, draft, blob))
        {
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        }

        var restricted = await db.RefusedAsync(MigratedDatabase.Api, "DELETE FROM image_blobs WHERE id = $1", blob);
        using (var publish = await seller.Http.PostCommandAsync($"/api/v1/auctions/{draft}/publish"))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        var frozen = await db.RefusedAsync(MigratedDatabase.Engine, "DELETE FROM auction_images WHERE auction_id = $1", draft);

        Assert.Equal("23001", restricted.SqlState); // restrict_violation: ON DELETE RESTRICT
        Assert.Equal("auction_images_frozen", frozen.ConstraintName);
    }

    private static async Task<Guid> UploadAsync(ApiUser user, byte[] data, string contentType)
    {
        using var response = await user.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(data, contentType), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> AttachAsync(ApiUser user, Guid draft, Guid image) =>
        user.Http.PostCommandAsync($"/api/v1/auctions/{draft}/images", new { imageId = image });
}
