# Build context: the repository root (see deploy/compose.yaml). Images pinned by digest (resolved 2026-11-17).
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /repo
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY eng/ eng/
COPY tests/Auction.ReferenceModel/Auction.ReferenceModel.csproj tests/Auction.ReferenceModel/packages.lock.json tests/Auction.ReferenceModel/
COPY src/Auction.Verifier/Auction.Verifier.csproj src/Auction.Verifier/packages.lock.json src/Auction.Verifier/
COPY src/Auction.Simulator/Auction.Simulator.csproj src/Auction.Simulator/packages.lock.json src/Auction.Simulator/
RUN dotnet restore --locked-mode src/Auction.Simulator/Auction.Simulator.csproj
COPY tests/Auction.ReferenceModel/ tests/Auction.ReferenceModel/
COPY src/Auction.Verifier/ src/Auction.Verifier/
COPY src/Auction.Simulator/ src/Auction.Simulator/
RUN dotnet publish src/Auction.Simulator/Auction.Simulator.csproj --no-restore -c Release -o /app

# Chiseled runtime: no shell or package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble-chiseled@sha256:1b234cef150624fe8cc7d027e8d742c0f03a16aef2484dc469eb8c872167255e
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Auction.Simulator.dll"]
