#!/usr/bin/env sh
# End-to-end tests of the containers: eng/e2e.sh [extra dotnet test arguments]
#
# Builds the images and starts a throwaway copy of the whole stack under its own Compose project, with
# its own generated secrets, ports and volumes, so it never touches the stack ./run.sh runs. Then runs
# tests/Auction.StackTests against it (the published port, a browser and the Docker CLI only) and removes
# it, data included. Set E2E_KEEP=1 to leave it running for a look afterwards.
#
#   E2E_PROJECT  Compose project name (default auction-e2e)
#   E2E_PORT     host port for the API; PostgreSQL and Redis take the next two (default 9280)
#
# Needs Docker with Compose v2 and the .NET SDK (on Windows, run it from Git Bash or WSL).
set -eu
cd "$(dirname "$0")/.."

project=${E2E_PROJECT:-auction-e2e}
port=${E2E_PORT:-9280}
work=TestResults/stack
env_file=$work/.env

secret() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 24
    else
        od -An -N24 -tx1 /dev/urandom | tr -d ' \n'
    fi
}

# The values Compose and the tests read; a fresh set every run.
rm -rf "$work"
mkdir -p "$work"
admin_password=$(secret)
{
    echo "PORT=$port"
    echo "POSTGRES_PORT=$((port + 1))"
    echo "REDIS_PORT=$((port + 2))"
    echo "DEMO_MODE=true"
    echo "SEED_DEMO=true"
    echo "ADMIN_PASSWORD=$admin_password"
    for name in DEMO_PASSWORD SIMULATOR_PASSWORD SIMULATOR_CONTROL_TOKEN POSTGRES_PASSWORD MIGRATOR_DB_PASSWORD \
        ENGINE_DB_PASSWORD API_DB_PASSWORD VERIFIER_DB_PASSWORD; do
        echo "$name=$(secret)"
    done
} > "$env_file"
chmod 600 "$env_file"

# The tests run docker themselves, so they get a path the Docker CLI understands on this machine.
absolute_env_file=$(cd "$work" && (pwd -W 2>/dev/null || pwd))/.env

compose() {
    docker compose -p "$project" --env-file "$env_file" -f deploy/compose.yaml "$@"
}

finish() {
    status=$?
    if [ "$status" -ne 0 ]; then
        echo "End-to-end run failed; the last of each container's log:"
        compose logs --no-color --tail 60 || true
    fi

    if [ "${E2E_KEEP:-}" = 1 ]; then
        echo "Left running (E2E_KEEP=1): http://localhost:$port, admin@auction.test / see $env_file"
        echo "Remove with: docker compose -p $project --env-file $env_file -f deploy/compose.yaml down -v"
    else
        compose down -v --remove-orphans >/dev/null 2>&1 || true
        rm -f "$env_file"
    fi

    exit "$status"
}
trap finish EXIT

# A leftover stack from an interrupted run would make the tests start from someone else's data.
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build --wait api redis simulator-control

dotnet build tests/Auction.StackTests -c Release

AUCTION_STACK_URL=http://localhost:$port \
AUCTION_ADMIN_PASSWORD=$admin_password \
AUCTION_COMPOSE_PROJECT=$project \
AUCTION_COMPOSE_ENV_FILE=$absolute_env_file \
    dotnet test --no-build -c Release --project tests/Auction.StackTests \
    --report-trx --results-directory "$work" -- --fail-skips on "$@"
