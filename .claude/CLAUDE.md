# Instructions for AI agents

Follow the engineering standards in `standards/` (a local, untracked, read-only copy) as amended by [ADR-0004](../docs/decisions/0004-process-records-outside-the-repo.md). In particular:

- Read `standards/ai-instructions.md`, `docs/requirements.md` and `docs/architecture.md` before changing anything.
- Write the failing test first, then the code. Run the gates below before calling anything done.
- Never report a command as successful unless you ran it and saw its output.
- Never push, publish or contact anyone without the owner's explicit permission. Never commit `_private/`.
- The repo holds system documentation only: what the system does, how it is built and why. Extend the existing doc that owns a topic instead of adding a file. Research, review reports, status and progress notes go in `Portfolio/planning/ebay/`.
- Write commit messages and code comments for a reader of the code: what changed and why. No process narration, review IDs or chat quotes.

## Project notes

- **Stack:** .NET 10 (ASP.NET Core API, separate gRPC auction engine), Blazor WebAssembly + MudBlazor, PostgreSQL 18, Redis 8 + SignalR, Docker Compose.
- **Local setup:** the .NET SDK 10.0.404 is installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet`; set `DOTNET_ROOT` to it and prepend it to `PATH`.

## Commands

- Restore: `dotnet restore --locked-mode` (after adding a package, run a plain `dotnet restore` to update the lock files)
- Format check: `dotnet format --verify-no-changes --no-restore`
- Build: `dotnet build --no-restore -c Release` (warnings are errors)
- Test: `dotnet test --no-build -c Release --report-trx --results-directory TestResults` (needs Docker)
- Infrastructure: `docker compose -f deploy/compose.yaml up -d postgres redis` (copy `deploy/.env.example` to `deploy/.env` and set the five passwords)
- Migrate: `docker compose -f deploy/compose.yaml up migrator` (exits 0; safe to repeat)
- New migration: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/Auction.Migrator --output-dir Migrations`; triggers and grants go in `Schema/` SQL called from the migration
