# Build context: the repository root (see deploy/compose.yaml). Images pinned by digest (resolved 2026-11-17).
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Auction.Engine.dll"]
