# Build context: the repository root (see deploy/compose.yaml). Images pinned by digest (resolved 2026-11-17).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /repo
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY eng/ eng/
COPY src/Auction.Domain/Auction.Domain.csproj src/Auction.Domain/packages.lock.json src/Auction.Domain/
COPY src/Auction.Engine/Auction.Engine.csproj src/Auction.Engine/packages.lock.json src/Auction.Engine/
RUN dotnet restore --locked-mode src/Auction.Engine/Auction.Engine.csproj
COPY src/Auction.Domain/ src/Auction.Domain/
COPY src/Auction.Contracts/ src/Auction.Contracts/
COPY src/Auction.Engine/ src/Auction.Engine/
RUN dotnet publish src/Auction.Engine/Auction.Engine.csproj --no-restore -c Release -o /app

# Chiseled ASP.NET runtime: no shell or package manager, runs as a non-root user, listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled@sha256:bcdfe6b8e923ad3d05f01dfa805ab97c99d742823ce030856002a6db38122712
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Auction.Engine.dll"]
