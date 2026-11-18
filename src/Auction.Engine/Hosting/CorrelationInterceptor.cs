using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Npgsql;

namespace Auction.Engine.Hosting;

/// <summary>
/// Puts the caller's correlation id (the <c>x-correlation-id</c> header, or a new one) on every log line
/// written while handling a call, and logs each call once, so an engine log can be matched to the API
/// request that caused it. A transient database failure (connection refused, too many connections)
/// becomes UNAVAILABLE, which the caller may retry with the same key; nothing was committed.
/// </summary>
internal sealed partial class CorrelationInterceptor(ILogger<CorrelationInterceptor> logger) : Interceptor
{
    public const string Header = "x-correlation-id";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var correlationId = context.RequestHeaders.GetValue(Header) is { Length: > 0 and <= 128 } header
            ? header
            : Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await continuation(request, context);
            LogHandled(logger, context.Method, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (NpgsqlException e) when (e.IsTransient)
        {
            LogDatabaseUnavailable(logger, context.Method, e);
            throw new RpcException(new Status(StatusCode.Unavailable, "The database is unavailable; retry with the same request_id."));
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Handled {Method} in {ElapsedMs} ms")]
    private static partial void LogHandled(ILogger logger, string method, long elapsedMs);

    [LoggerMessage(2, LogLevel.Error, "{Method} failed: the database is unavailable")]
    private static partial void LogDatabaseUnavailable(ILogger logger, string method, Exception exception);
}
