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
| Live updates, simulator, admin | Not started |

## Run it

Prerequisites: Docker with Compose v2 (on Windows, use Git Bash or WSL).

```bash
./run.sh
```

Then open <http://localhost:8080>. The first run creates `deploy/.env` with a generated value for every secret, prints the admin password once and keeps the seeded accounts' passwords in `deploy/.demo-credentials` (both git-ignored). It builds the images, applies the migrations, adds the demo accounts and six active auctions, and waits until every service is healthy. Create your own account on the sign-in page with a display name and an email; sign in from a second browser as another user to bid against yourself.

To use other host ports, set them in the shell or in `deploy/.env` (`PORT=9080 ./run.sh`). Stop with `docker compose -f deploy/compose.yaml down`, adding `-v` to delete the data. [`Auction.Api.http`](src/Auction.Api/Auction.Api.http) walks the same journey over plain HTTP.

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

## Verification

- **Unit tests** for every auction rule. Each rejection is paired with a control case that must succeed.
- **Property tests** (CsCheck): each run generates 2,000 random auctions with random terms and bid sequences, and checks the rules against a separate naive reference model.
- **Planted-bug checks:** deliberate bugs planted during development were each caught by the suite: seven in the rules, three in the database triggers and grants, and five in the engine (no row lock, the clock read before the lock wait, a private amount written to the history, a close worker that skips the lock, and one that closes early), two in the gRPC mapping, and six more for idempotency, draft privacy, event payloads and grants (an idempotency key that ignores a clash on another auction, other users' drafts made visible, a bidder id in an event, a sweep that deletes fresh keys, a worker that never sweeps, and over-granted roles), and ten in the API (a foreign origin or a form post accepted, the reserve shown to everyone, drafts readable by anyone, email-only sign-in for password accounts, ownership revealed with 403, an uncapped page size, an extra query per page, a `SameSite=Lax` cookie, and an admin allowed to sell), and eight for the web client and seed (another seller's auctions in "my auctions", every bid shown as leading, a minimum bid without the increment, a page that may be framed, the app's page served for an unknown API path, a seed that resets passwords, the opening price shown after a bid, and a repeated sign-up not offered the existing account).
- **Real infrastructure:** database tests run against PostgreSQL 18, never an in-memory provider.
- **Browser tests** (Playwright, headless Chromium): a seller lists and publishes, a buyer bids, and both see the new price after a reload, in the in-process site and again against the one-command Compose stack.
- **CI** runs a locked restore, a format check, a zero-warning Release build, the one command on the fresh clone, the full suite and the browser journey against that stack, and lints the gRPC contract (with a breaking-change check on pull requests). It fails on any floating-point type in money code.
- **Independent requirements audits:** the latest one confirms the rules are correct at the unit layer. The requirements stay open until the database and service layers are built and tested.

## Repository layout

```
src/        application code (today: Auction.Domain, Auction.Migrator, Auction.Contracts, Auction.Engine, Auction.Api, Auction.Web)
tests/      unit, property, integration and browser tests, and the reference model
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
- Prices update on reload; live updates arrive with build step 9. A draft can't be deleted yet, only edited, published or left.
- The rules, database, engine, API and a plain web client are built; live updates, the simulator, the admin area, images and the visual design are planned in the build plan.

Items deliberately left out are listed in the [requirements](docs/requirements.md) §4 under Should, Could and Won't.
