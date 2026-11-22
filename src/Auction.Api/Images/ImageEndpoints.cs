using System.Text.Json;
using Auction.Api.Accounts;
using Auction.Api.Auctions;
using Auction.Api.Hosting;
using Auction.Contracts.V1;
using Microsoft.Net.Http.Headers;

namespace Auction.Api.Images;

/// <summary>An upload: the image's type as the browser claims it, and its bytes in base64, inside JSON (ADR-0003).</summary>
public sealed record UploadRequest(string? ContentType, string? DataBase64);

public sealed record UploadedImage(Guid Id, string ContentType, int Width, int Height);

public sealed record ImageRequest(Guid? ImageId);

public sealed record ImageOrder(IReadOnlyList<Guid>? ImageIds);

/// <summary>
/// Images (UI-05) in two steps, so the engine stays the single writer of auction state: the API stores the
/// bytes as a blob owned by the uploader, then engine commands link blobs to a draft under its lock.
/// Serving uses the validated type with nosniff, an ETag from the content hash, and caching that never lets
/// a proxy keep a draft's image.
/// </summary>
internal static class ImageEndpoints
{
    public static RouteGroupBuilder MapImages(this RouteGroupBuilder api)
    {
        var images = api.MapGroup("/images").WithTags("Images");
        images.MapPost("", UploadAsync).RequireAuthorization().Limited(RateLimits.Policy.Write)
            .Accepts<UploadRequest>("application/json")
            .Produces<UploadedImage>(StatusCodes.Status201Created)
            .Produces<UploadedImage>()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status507InsufficientStorage);
        images.MapGet("/{id:guid}", ServeAsync).Produces<byte[]>(StatusCodes.Status200OK, ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP).ProducesProblem(StatusCodes.Status404NotFound);

        var links = api.MapGroup("/auctions/{auctionId:guid}/images").WithTags("Images").RequireAuthorization().Limited(RateLimits.Policy.Write)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        links.MapPost("", (Guid auctionId, ImageRequest body, HttpContext http, EngineGateway engine) =>
            SendAsync(http, engine, body.ImageId, async (client, key, actor, call) => (await client.AttachImageAsync(
                new AttachImageRequest { RequestId = key, ActorId = actor, AuctionId = auctionId.ToString(), ImageId = body.ImageId.ToString() }, call)).Result));
        links.MapDelete("/{imageId:guid}", (Guid auctionId, Guid imageId, HttpContext http, EngineGateway engine) =>
            SendAsync(http, engine, imageId, async (client, key, actor, call) => (await client.RemoveImageAsync(
                new RemoveImageRequest { RequestId = key, ActorId = actor, AuctionId = auctionId.ToString(), ImageId = imageId.ToString() }, call)).Result));
        links.MapPost("/{imageId:guid}/cover", (Guid auctionId, Guid imageId, HttpContext http, EngineGateway engine) =>
            SendAsync(http, engine, imageId, async (client, key, actor, call) => (await client.SetCoverImageAsync(
                new SetCoverImageRequest { RequestId = key, ActorId = actor, AuctionId = auctionId.ToString(), ImageId = imageId.ToString() }, call)).Result));
        links.MapPut("", (Guid auctionId, ImageOrder body, HttpContext http, EngineGateway engine) =>
            SendAsync(http, engine, Guid.Empty, async (client, key, actor, call) => (await client.ReorderImagesAsync(
                new ReorderImagesRequest { RequestId = key, ActorId = actor, AuctionId = auctionId.ToString(), ImageIds = { (body.ImageIds ?? []).Select(id => id.ToString()) } }, call)).Result));
        return api;
    }

    private static async Task<IResult> UploadAsync(HttpContext http, ImageStore store)
    {
        if (http.User.IsAdmin())
        {
            return Problems.Of(StatusCodes.Status403Forbidden, "admin_only", "Admin accounts have admin privileges only; they can't sell.");
        }

        if (http.Request.ContentLength > ImageOptions.MaxRequestBytes)
        {
            return TooLarge();
        }

        UploadRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<UploadRequest>(
                new LimitedStream(http.Request.Body, ImageOptions.MaxRequestBytes), JsonSerializerOptions.Web, http.RequestAborted);
        }
        catch (LimitedStream.TooLongException)
        {
            return TooLarge();
        }
        catch (JsonException)
        {
            return Invalid("dataBase64", "invalid");
        }

        if (Decode(body?.DataBase64) is not { } data)
        {
            return Invalid("dataBase64", "invalid");
        }

        if (data.Length > ImageOptions.MaxImageBytes)
        {
            return Invalid("dataBase64", "too_large");
        }

        // The file's own bytes decide its type; a claimed type that disagrees is refused.
        if (await ImageFormat.NormalizeAsync(data, body!.ContentType, http.RequestAborted) is not { } photo)
        {
            return Invalid("contentType", "unsupported_or_mismatched");
        }

        var info = photo.Info;
        var (result, id) = await store.UploadAsync(http.User.UserIdOf()!.Value, photo.Data, info, http.RequestAborted);
        var uploaded = id is { } blob ? new UploadedImage(blob, info.ContentType, info.Width, info.Height) : null;
        return result switch
        {
            UploadResult.Stored => TypedResults.Created($"/api/v1/images/{id}", uploaded),
            UploadResult.Existing => TypedResults.Ok(uploaded),
            UploadResult.RateLimited => Problems.Of(StatusCodes.Status429TooManyRequests, "upload_rate", "Too many uploads this hour; try again later."),
            UploadResult.UserQuotaFull => Problems.Of(StatusCodes.Status507InsufficientStorage, "quota_full", "Your image storage is full; remove unused images first."),
            _ => Problems.Of(StatusCodes.Status507InsufficientStorage, "storage_full", "Image storage for the demo is full until the nightly reset."),
        };
    }

    private static async Task<IResult> ServeAsync(Guid id, HttpContext http, ImageStore store)
    {
        if (await store.FindAsync(id, http.User.UserIdOf(), http.RequestAborted) is not { } image)
        {
            return Problems.Of(StatusCodes.Status404NotFound, "not_found", "No such image.");
        }

        var headers = http.Response.Headers;
        headers.ETag = image.ETag;
        headers.CacheControl = image.Public ? "public, max-age=31536000, immutable" : "private, no-store";
        return http.Request.Headers.IfNoneMatch.ToString() == image.ETag
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.File(image.Data, image.ContentType);
    }

    private static async Task<IResult> SendAsync(
        HttpContext http, EngineGateway engine, Guid? imageId,
        Func<AuctionCommandService.AuctionCommandServiceClient, string, string, Grpc.Core.CallOptions, Task<CommandResult>> call)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        if (imageId is null)
        {
            return Invalid("imageId", "required");
        }

        var actor = EngineGateway.Actor(http);
        return await engine.SendAsync(
            http, options => call(engine.Client, key, actor, options), result => TypedResults.Ok(EngineGateway.Response(result, actor)));
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        var buffer = new byte[base64.Length * 3 / 4];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? buffer[..written] : null;
    }

    private static IResult Invalid(string field, string code) => Problems.Validation(new Dictionary<string, string[]> { [field] = [code] });

    private static IResult TooLarge() =>
        Problems.Of(StatusCodes.Status413PayloadTooLarge, "request_too_large", "The upload is over 8 MiB; images may be at most 5 MiB.");

    /// <summary>Reads at most <paramref name="limit"/> bytes, then refuses, so a body without a length can't run on.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public sealed class TooLongException : IOException;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            return _read > limit ? throw new TooLongException() : count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
