using Microsoft.EntityFrameworkCore.Design;

namespace Auction.Migrator;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a live database.</summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<AuctionDbContext>
{
    public AuctionDbContext CreateDbContext(string[] args) =>
        AuctionDbContext.Create("Host=localhost;Database=auction;Username=migrator");
}
