# Distributed Auction Platform

An eBay-style timed auction platform in .NET 10. Buy, Sell and Admin views stay consistent across many simultaneous users, and a load simulator with an independent verifier checks every auction's outcome afterwards.

## Status

Built bottom-up, one tested layer at a time ([build plan](docs/build-plan.md)).

| Layer | State |
|---|---|
| Toolchain and CI: pinned SDK, locked packages, zero-warning build, real-PostgreSQL tests | Built; CI green |
| Auction rules: money, proxy and fixed-increment bidding, reserve, deadlines, listing validation, lifecycle | Built and unit-tested (88 tests) |
| Database: schema, four least-privilege roles, append-only bids, amount and lifecycle constraints, one-shot migrator | Built and tested against PostgreSQL 18 |
| Engine core: create, edit, publish, cancel and bid, each one locked transaction with idempotency and an outbox event | Built and tested against PostgreSQL 18, including 50-bidder races checked against the reference model |
| Close worker: starts scheduled auctions, closes ended ones with Sold, Reserve not met or No bids, and deletes idempotency records after 24 hours | Built and tested, including two workers sweeping at once |
| Engine service: the `auction.v1` gRPC contract, health probe, correlated JSON logs, internal-only in Compose | Built and tested from its container image, including the 50-bidder races over gRPC |
| API: sign-up and email sign-in, cookie sessions, auction list, detail and bid history, and create, edit, publish, cancel and bid through the engine, with Problem Details, cross-site checks and an OpenAPI document | Built and tested over HTTP against the engine container |
| Web client: Blazor WebAssembly and MudBlazor pages for sign-in, the directory, an auction with its bid box and history, selling (drafts, publish, cancel) and my bids, served by the API from the same origin | Built and tested in a real browser (Playwright) against the whole stack; plain styling until build step 15 |
| One-command run with generated secrets and demo seed data | Built; CI runs it on every push from a fresh clone |
| Live updates: outbox relay in commit order, SignalR hints through a Redis backplane, version-gated views that reconcile every 10 s | Built and tested: four browsers converge within 2 s; with Redis stopped, bids still work and views heal |
| Simulator and verifier: virtual users over the public API (smoke, realistic and hot-burst profiles) and a read-only checker that recomputes every auction with a naive reference model | Built and tested: 50 bidders on one auction for 30 seconds with zero invariant violations |
| Admin area: live activity, system health, latency KPIs over a rolling five minutes, auction moderation (suspend, resume, cancel with a reason), user disable/enable, starting and stopping a simulator run, and a searchable audit log of every admin action | Built and tested in the browser and over HTTP |
| Accounts: one active session per account (signing in elsewhere ends the others, and an open page is told at once), a live notification bell, and sign-in, sign-up and write rate limits | Built and tested in the browser and over HTTP |
| Images: up to four JPEG, PNG or WebP per listing, checked by file signature and size, stored in PostgreSQL, linked by the engine under the auction lock, frozen at publish, with race-free quotas and an hourly sweep | Built and tested, including racing attaches and quota races |
| Redis jobs: a version-checked read cache for auctions and the directory, the session-validity cache, shared rate-limit counters and presence across instances, with PostgreSQL or local fallbacks; directory search and status filters | Built and tested, including two API instances sharing limits and presence, and a Redis outage |
| Design: a themed MudBlazor layout, a card directory with cover images, shared loading, empty, error (with retry) and sign-in states, usable at 360 px | Built and tested: zero serious or critical axe violations on the main pages, no sideways scrolling at 360 px |
| Release: liveness and readiness, a path base and trusted proxies for hosting, reset-demo, and supply-chain checks in CI (gitleaks, Trivy, SBOMs with provenance, Dependabot) | Built and tested; the release tag is the owner's to create |
| Observability: OpenTelemetry traces, metrics and logs from the API and engine over OTLP, with the standalone Aspire dashboard in an optional Compose profile | Built and tested: a bid's trace runs from the API request through the engine's transaction to its queries |

## Run it

Prerequisites: Docker with Compose v2 (on Windows, use Git Bash or WSL).

```bash
./run.sh
```

Then open <http://localhost:8080>. The first run creates `deploy/.env` with a generated value for every secret, prints the admin password once and keeps the seeded accounts' passwords in `deploy/.demo-credentials` (both git-ignored). It builds the images, applies the migrations, adds the demo accounts and six active auctions, and waits until every service is healthy. Create your own account on the sign-in page with a display name and an email; sign in from a second browser as another user to bid against yourself. To see live bidding, run the simulator (below).

To use other host ports, set them in the shell or in `deploy/.env` (`PORT=9080 ./run.sh`). To return the demo to its seeded state (visitors' accounts, bids and uploads removed; seeded passwords kept): `docker compose -f deploy/compose.yaml run --rm migrator reset-demo`. Behind a reverse proxy, set `PATH_BASE` (for example `/projects/ebay`) and `TRUSTED_PROXIES` (addresses or CIDR ranges) in `deploy/.env`. When the proxy or tunnel runs in its own Compose project, add `-f deploy/compose.edge.yaml` and set `EDGE_NETWORK` to a network that holds only that proxy: the API joins it as `auction-api` and needs no published port. Stop with `docker compose -f deploy/compose.yaml down`, adding `-v` to delete the data. [`Auction.Api.http`](src/Auction.Api/Auction.Api.http) walks the same journey over plain HTTP.

To watch traces, metrics and logs, start the optional Aspire dashboard and point the services at it, then open <http://localhost:18888>:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889 docker compose -f deploy/compose.yaml --profile observability up -d
```

## Run the simulator

With the stack running:

```bash
docker compose -f deploy/compose.yaml run --rm simulator run realistic
```

Profiles: `smoke` (one auction, three bidders, 15 s), `realistic` (five auctions, twenty bidders at a human pace, 60 s) and `hot-burst` (fifty bidders on one auction as fast as they can, 30 s). The simulated seller lists the auctions, the bidders sign in to reserved simulator accounts with the ordinary password sign-in, and every bid goes through the public JSON API like a browser's. When the auctions close, the verifier, connecting as the read-only `verifier` role, recomputes each one from its history with the reference model. The JSON report shows intended against actual load, refusals by reason, bid latency p50 and p95, each auction's result and the verifier's findings. The exit code is non-zero on any violation. Simulated bidders show as "Bidder n (simulated)" in bid histories. Options: `--seed N`, `--duration SECONDS`, `--report FILE`. An admin can also start the smoke or realistic profile from the Activity page and stop it early; that run goes through the simulator's control service, which only the API can reach. Only one run happens at a time, whether started there or from the command line.

The verifier also runs on its own over the whole database: `VERIFIER_CONNECTION_STRING=... dotnet run --project src/Auction.Verifier [-- --since 2026-11-18T00:00:00Z]`.

## How the auctions work

The full rules, with their reasons and legal basis, are in the [requirements](docs/requirements.md) §5 and [compliance notes](docs/compliance.md). In short:

- **Timed, ascending-price, one item, US dollars.** Amounts are whole cents end to end.
- **The seller picks two settings per auction:** a price floor (a hidden reserve, a published minimum bid, or no reserve) and a bidding mode (proxy, where bidders set a private maximum and the system bids for them, or fixed steps of one increment).
- **Proxy price:** the highest maximum leads at the second-highest maximum plus one increment, capped at the leader's maximum; equal maxima go to the earlier one. Under a reserve, the price jumps to the reserve when a maximum first reaches it.
- **Deadlines use the database clock**, read after the auction's lock is taken, so a bid that waited past the end is refused. The close worker ends each auction as Sold, Reserve not met or No bids.
- **Terms freeze at publish.** A seller can't bid on their own auction, and can cancel only before the first accepted bid. Drafts are private to their seller.
- **Bidders are anonymous** in public histories ("Bidder 1", "You"); maxima and reserves are never shown.

## Architecture

```mermaid
flowchart LR
  browser([Browsers]) --> web[Blazor WASM + MudBlazor]
  web -->|REST + SignalR| api[ASP.NET Core API]
  sim[CLI simulator] -->|same public API| api
  api -->|gRPC| engine[Auction engine<br/>single writer]
  engine -->|one locked transaction per command| pg[(PostgreSQL 18)]
  api -->|reads, outbox relay| pg
  api <-->|cache, rate limits, backplane| redis[(Redis 8)]
  verifier[Verifier] -->|read-only| pg
```

- **One writer.** Only the engine changes auction state. A bid, any automatic proxy counter-bids, the auction update, the idempotency record and an outbox event commit in one transaction, under a row lock on the auction.
- **Database time decides deadlines.** The engine reads `clock_timestamp()` after taking the lock, so a bid that waited on the lock past the end is refused.
- **Live views without trusting the message.** An outbox relay sends SignalR change hints in commit order. Clients re-fetch authoritative state and ignore anything older than what they already show.
- **Redis helps but never decides.** With Redis down, bidding still works and the admin health panel shows what degraded.
- **An independent check.** After a simulator run, a separate verifier recomputes every auction from its bid history with a deliberately naive reference model and reports any disagreement.

## Run the tests

Prerequisites: .NET SDK 10.0.404 and Docker (the integration tests start PostgreSQL 18 and the engine container through Testcontainers; the browser tests download Chromium on first use).

```bash
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
```

To run the browser tests against a stack that is already running, set `AUCTION_BASE_URL` (for example `http://localhost:8080`) and filter to them: `dotnet test --no-build -c Release --project tests/Auction.IntegrationTests -- --filter-class "*JourneyTests"`.

To test the containers end to end:

```bash
sh eng/e2e.sh
```

It builds the images, starts a throwaway copy of the whole stack under its own Compose project (ports 9280 to 9282, fresh secrets and volumes, separate from the one `./run.sh` runs), runs `tests/Auction.StackTests` against it from outside, and removes it. `E2E_KEEP=1` leaves it running. Without a stack those tests are skipped; under `eng/e2e.sh` a skipped test fails the run.

## Verification

- **Unit tests** for every auction rule. Each rejection is paired with a control case that must succeed.
- **Property tests** (CsCheck): each run generates 2,000 random auctions with random terms and bid sequences, and checks the rules against a separate naive reference model.
- **Planted-bug checks:** deliberate bugs planted during development were each caught by the suite: seven in the rules, three in the database triggers and grants, and five in the engine (no row lock, the clock read before the lock wait, a private amount written to the history, a close worker that skips the lock, and one that closes early), two in the gRPC mapping, and six more for idempotency, draft privacy, event payloads and grants (an idempotency key that ignores a clash on another auction, other users' drafts made visible, a bidder id in an event, a sweep that deletes fresh keys, a worker that never sweeps, and over-granted roles), and ten in the API (a foreign origin or a form post accepted, the reserve shown to everyone, drafts readable by anyone, email-only sign-in for password accounts, ownership revealed with 403, an uncapped page size, an extra query per page, a `SameSite=Lax` cookie, and an admin allowed to sell), and eight for the web client and seed (another seller's auctions in "my auctions", every bid shown as leading, a minimum bid without the increment, a page that may be framed, the app's page served for an unknown API path, a seed that resets passwords, the opening price shown after a bid, and a repeated sign-up not offered the existing account), and seven for live updates (a relay that ignores the snapshot horizon or waits only for NOTIFY, a hub whose WebSocket handshake skips the Origin check, an auction page or directory that ignores hints, no periodic reconcile, and an older answer replacing a newer one). Four more for the simulator and verifier: an engine with the auction row lock removed (the 50-bidder hot burst exposes it), a verifier that skips the standing or the visible-price check, and a run that passes without a clean verifier report. Seven for the admin area: admin routes open to any signed-in user, moderation without an audit row, an outbid notice sent to the new leader, a disabled user keeping their session, a KPI window one second too long, drafts in the moderation list, and the Sell area shown to admins. Eight for accounts and notices: a sign-in that leaves other sessions alive, a sign-out that only drops the cookie, revoked sessions still accepted, anyone marking any notification read, a bell never told, writes without a rate limit, no lockout after wrong passwords, and an open page that ignores the sign-out message. Eight for images: an upload that trusts the claimed type, an attach that ignores who owns the blob, a fifth image allowed, draft images cached publicly, a quota checked without its row lock, a sweep that ignores links, a 21st publish in a day, and published images not frozen in the database. Six for the Redis jobs: no version announce when a command is accepted (exposed by holding the relay back), a revoked session left in the cache, limits and presence counted per instance only, an "ending soon" filter without its window, and a sign-in that fails while Redis is down. Four for the design: an error with no way to retry, a bell button with no accessible name (axe), cards too wide for a phone, and drafts that can't be deleted. Five for hosting: readiness that ignores the engine, forwarded headers believed from anyone, a reset that keeps visitors' accounts, a page base that ignores the path base, and a cookie scoped to the whole host. Eight for the admin simulator control, the audit log and telemetry: an API that sends the engine no trace context, a control endpoint that accepts any token, a demo that allows the hot burst, simulator starts and stops left out of the audit, an audit log listed oldest first, an engine command without its transaction span, a publish batch without its span, and telemetry that is never exported.
- **Real infrastructure:** database tests run against PostgreSQL 18, never an in-memory provider.
- **Browser tests** (Playwright, headless Chromium): a seller lists and publishes, a buyer bids, and both see the new price after a reload, in the in-process site and again against the one-command Compose stack.
- **End-to-end tests of the containers** (`eng/e2e.sh`), with no project references, only the published port, a browser and the Docker CLI: every service healthy and the migrator exited cleanly, no application container running as root, every long-running service set to restart with Docker, only the API and the data stores publishing ports and only on 127.0.0.1, a replaced API container that keeps people signed in and their listings, bids accepted after the engine restarts, live prices in three browsers within 2 s, moderation by the seeded admin with its audit row, and a smoke run from the admin page that the verifier passes.
- **CI** runs a locked restore, a format check, a zero-warning Release build, the one command on the fresh clone, the full suite, the browser journey against that stack and the end-to-end tests of the containers, and lints the gRPC contract (with a breaking-change check on pull requests). It fails on any floating-point type in money code. A supply-chain job scans the whole history for secrets (gitleaks), scans every image for fixable critical vulnerabilities (Trivy), writes SPDX SBOMs and, on `main`, attests their provenance; Dependabot proposes updates weekly.
- **Independent requirements audits:** the latest one confirms the rules are correct at the unit layer. The requirements stay open until the database and service layers are built and tested.

## Repository layout

```
src/        application code (Auction.Domain, Auction.Migrator, Auction.Contracts, Auction.Engine, Auction.Api, Auction.Web, Auction.Simulator, Auction.Verifier)
tests/      unit, property, integration, browser and end-to-end tests, and the reference model
run.sh      the one command
docs/       requirements, architecture, build plan, decision records
deploy/     Docker Compose and the environment template
eng/        analyzer thresholds
```

## Documentation

| Document | What it covers |
|---|---|
| [Requirements](docs/requirements.md) | Requirements with acceptance criteria, domain rules, authorization table, targets, traceability |
| [Architecture](docs/architecture.md) | Components, the bid flow, engineering rules, design decisions, deployment |
| [Build plan](docs/build-plan.md) | Build order, dependencies, schedule and cut order |
| [User stories](docs/user-stories.md) | Stories for the Buy, Sell and Admin views |
| [Auction law and platform rules](docs/compliance.md) | What a real marketplace must do, and how this one handles each rule |
| [Decision records](docs/decisions/) | The significant decisions, with the alternatives considered |

## Known limitations

- Not a real marketplace: no payments, shipping or email.
- One API instance in Compose. Two would need shared Data Protection keys for the cookie (SEC-03, the RT-03 stretch goal); Redis already shares hints, limits and presence between instances.
- Without Redis, rate limits are counted per instance and live hints stop until it returns; pages still reconcile every 10 seconds.
- Times show in the browser's time zone; amounts in US dollars only.
- The Aspire dashboard is a developer tool on this machine (no sign-in, bound to 127.0.0.1); the admin KPIs never depend on it.

Items deliberately left out are listed in the [requirements](docs/requirements.md) §4 under Should, Could and Won't.
