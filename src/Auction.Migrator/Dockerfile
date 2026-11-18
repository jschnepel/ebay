# Build context: the repository root (see deploy/compose.yaml). Images pinned by digest (resolved 2026-11-17).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /repo
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY eng/ eng/
COPY src/Auction.Migrator/Auction.Migrator.csproj src/Auction.Migrator/packages.lock.json src/Auction.Migrator/
RUN dotnet restore --locked-mode src/Auction.Migrator/Auction.Migrator.csproj
COPY src/Auction.Migrator/ src/Auction.Migrator/
RUN dotnet publish src/Auction.Migrator/Auction.Migrator.csproj --no-restore -c Release -o /app

# Chiseled runtime: no shell or package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled@sha256:1b234cef150624fe8cc7d027e8d742c0f03a16aef2484dc469eb8c872167255e
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Auction.Migrator.dll"]
