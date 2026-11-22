#!/usr/bin/env sh
# The one command: ./run.sh
#
# On the first run it creates deploy/.env from deploy/.env.example with a generated value for every
# required secret and keeps the seeded accounts' passwords in deploy/.demo-credentials (both files
# are git-ignored). Then it builds and starts the whole stack and waits until it is healthy.
# Needs Docker with Compose v2 (on Windows, run it from Git Bash or WSL).
set -eu
cd "$(dirname "$0")"

env_file=deploy/.env
credentials=deploy/.demo-credentials

secret() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 24
    else
        od -An -N24 -tx1 /dev/urandom | tr -d ' \n'
    fi
}

if [ ! -f "$env_file" ]; then
    work="$env_file.tmp"
    cp deploy/.env.example "$work"
    for name in POSTGRES_PASSWORD MIGRATOR_DB_PASSWORD ENGINE_DB_PASSWORD API_DB_PASSWORD VERIFIER_DB_PASSWORD ADMIN_PASSWORD DEMO_PASSWORD SIMULATOR_PASSWORD SIMULATOR_CONTROL_TOKEN; do
        value=$(secret)
        # Fills only an empty value, keeping the line's comment.
        sed "s|^$name=[[:space:]]*#|$name=$value  #|; s|^$name=[[:space:]]*\$|$name=$value|" "$work" > "$work.next"
        mv "$work.next" "$work"
        if [ "$name" = ADMIN_PASSWORD ]; then admin_password=$value; fi
        if [ "$name" = DEMO_PASSWORD ]; then demo_password=$value; fi
    done
    mv "$work" "$env_file"
    chmod 600 "$env_file"

    umask 077
    {
        echo "admin@auction.test  $admin_password"
        echo "seller@auction.test $demo_password"
        echo "buyer@auction.test  $demo_password"
    } > "$credentials"
    echo "Created $env_file with generated secrets."
    echo "Demo sign-in credentials are in $credentials."
fi

# Secrets added after an existing .env was created are appended, so an older .env keeps working.
for name in SIMULATOR_CONTROL_TOKEN; do
    if ! grep -q "^$name=[^[:space:]#]" "$env_file"; then
        sed -i.bak "/^$name=/d" "$env_file" && rm -f "$env_file.bak"
        echo "$name=$(secret)" >> "$env_file"
        echo "Added a generated $name to $env_file."
    fi
done

sh deploy/prepare-engine-tls.sh
docker compose -f deploy/compose.yaml up -d --build --wait api redis simulator-control

# A PORT set in the shell wins over deploy/.env, as it does for Compose.
port=${PORT:-$(sed -n 's/^PORT=\([0-9]*\).*/\1/p' "$env_file")}
echo "Running: open http://localhost:${port:-8080}"
echo "Stop with: docker compose -f deploy/compose.yaml down (add -v to delete the data)"
