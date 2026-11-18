using Auction.Domain;
using Npgsql;

namespace Auction.Engine;

/// <summary>
/// The auction commands. Each runs as one transaction that locks the auction row, reads the database
/// clock after the lock, replays a repeated idempotency key, applies the domain rules, and commits the
/// state change, bid history, command outcome and outbox event together.
/// </summary>
public sealed class AuctionEngine(NpgsqlDataSource dataSource, EngineOptions options)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly EngineOptions _options = options;

    public Task<CommandOutcome> CreateDraftAsync(CommandContext context, AuctionTerms terms, CancellationToken cancellationToken) =>
        NotBuiltYetAsync(context);

    public Task<CommandOutcome> EditDraftAsync(
        CommandContext context, AuctionId auctionId, AuctionTerms terms, CancellationToken cancellationToken) =>
        NotBuiltYetAsync(context);

    public Task<CommandOutcome> PublishAsync(CommandContext context, AuctionId auctionId, CancellationToken cancellationToken) =>
        NotBuiltYetAsync(context);

    public Task<CommandOutcome> CancelAsync(CommandContext context, AuctionId auctionId, CancellationToken cancellationToken) =>
        NotBuiltYetAsync(context);

    public Task<CommandOutcome> PlaceBidAsync(
        CommandContext context, AuctionId auctionId, Money amount, CancellationToken cancellationToken) =>
        NotBuiltYetAsync(context);

    private Task<CommandOutcome> NotBuiltYetAsync(CommandContext context) =>
        throw new NotImplementedException($"{context} on {_dataSource.ConnectionString} with {_options}");
}
