#!/usr/bin/env sh
# Pull-based delivery for the hosted copy: deploy/deploy.sh [--check | --to <commit>]
#
# Runs on a timer on the host. Each run asks GitHub for the newest commit on main whose CI run passed
# (that run also published its images to GHCR). If it isn't the commit already live, it fetches that
# commit's Compose files, pulls its images, starts them and checks the public site. If the switch or the
# check fails, it starts the previous commit again and exits non-zero. Nothing reaches into the host:
# it needs only outbound HTTPS and Docker.
#
#   --check        say which commit would deploy and whether its images exist; change nothing
#   --to <commit>  deploy this commit (its images must exist) instead of the newest passing one
#
#   DEPLOY_HOME     the deployment's own folder: .env, releases/, current, previous, deploy.log
#                   (default ~/.auction-live)
#   DEPLOY_REPO     owner/name on GitHub (default jschnepel/ebay)
#   DEPLOY_PROJECT  Compose project name (default auction-live)
#   DEPLOY_URL      the address checked after a switch (default https://auction.schnepel.cloud)
#   DEPLOY_SOURCE   a local deploy/ folder to take the Compose files from instead of GitHub (for testing)
#
# The timer runs the copy in DEPLOY_HOME, first moving deploy.sh.next over it when a release staged one:
#   cd ~/.auction-live && { [ ! -f deploy.sh.next ] || mv deploy.sh.next deploy.sh; } && sh deploy.sh
#
# Schema changes must stay readable by the previous release, because a rollback runs the previous images
# against a database the new migrator has already moved forward.
set -eu

home=${DEPLOY_HOME:-$HOME/.auction-live}
repo=${DEPLOY_REPO:-jschnepel/ebay}
project=${DEPLOY_PROJECT:-auction-live}
url=${DEPLOY_URL:-https://auction.schnepel.cloud}
registry=${IMAGE_REGISTRY:-ghcr.io/$(echo "${repo%%/*}" | tr '[:upper:]' '[:lower:]')}
keep=3

# Writes to the log, skipping a message identical to the last one so a waiting timer doesn't fill it.
log() {
    echo "$*"
    last=$(tail -n 1 "$home/deploy.log" 2>/dev/null | cut -d' ' -f2-)
    [ "$last" = "$*" ] || echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $*" >> "$home/deploy.log"
}

# The head commit of the newest successful CI run for a push to main, or nothing.
newest_passing() {
    curl -fsS --max-time 20 -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/$repo/actions/workflows/ci.yml/runs?branch=main&event=push&status=success&per_page=1" \
        | sed -n 's/^ *"head_sha": *"\([0-9a-f]\{40\}\)".*/\1/p' | head -n 1
}

images() {
    for name in migrator engine api simulator; do
        echo "$registry/auction-$name:sha-$1"
    done
}

# True when every image for the commit is on this machine, pulling the ones that aren't.
have_images() {
    for image in $(images "$1"); do
        docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null 2>&1 || return 1
    done
}

# The commit's own Compose files, so a release never depends on what this machine has checked out.
fetch_release() {
    dir="$home/releases/$1"
    [ -f "$dir/.complete" ] && return 0
    rm -rf "$dir"
    mkdir -p "$dir/postgres"
    for file in compose.yaml compose.edge.yaml compose.release.yaml postgres/init-roles.sh deploy.sh; do
        if [ -n "${DEPLOY_SOURCE:-}" ]; then
            cp "$DEPLOY_SOURCE/$file" "$dir/$file" || return 1
        else
            curl -fsSL --max-time 30 "https://raw.githubusercontent.com/$repo/$1/deploy/$file" -o "$dir/$file" || return 1
        fi
    done
    touch "$dir/.complete"
}

compose() {
    sha=$1
    shift
    dir="$home/releases/$sha"
    IMAGE_TAG="sha-$sha" IMAGE_REGISTRY="$registry" docker compose -p "$project" --env-file "$home/.env" \
        -f "$dir/compose.yaml" -f "$dir/compose.edge.yaml" -f "$dir/compose.release.yaml" "$@"
}

start() {
    compose "$1" up -d --wait api redis simulator-control demo-clock
}

# The public site answers: ready (PostgreSQL and the engine reachable), the page, sign-in and the API.
healthy() {
    tries=0
    until curl -fsS -o /dev/null --max-time 10 "$url/ready"; do
        tries=$((tries + 1))
        [ "$tries" -ge 30 ] && return 1
        sleep 2
    done
    for path in / /login /api/v1/auctions; do
        curl -fsS -o /dev/null --max-time 15 "$url$path" || return 1
    done
}

# Keeps the newest few releases' files and images; older images are removed to save disk.
prune() {
    ls -1t "$home/releases" | tail -n +$((keep + 1)) | while read -r old; do
        [ "$old" = "$1" ] || [ "$old" = "$2" ] || {
            for image in $(images "$old"); do docker image rm "$image" >/dev/null 2>&1 || true; done
            rm -rf "${home:?}/releases/$old"
        }
    done
}

mkdir -p "$home/releases"
[ -f "$home/.env" ] || { echo "No $home/.env: the deployment's settings and secrets go there." >&2; exit 2; }

mode=deploy
target=
case "${1:-}" in
    --check) mode=check ;;
    --to) target=${2:?--to needs a commit}; mode=deploy ;;
    "") ;;
    *) echo "Usage: deploy/deploy.sh [--check | --to <commit>]" >&2; exit 2 ;;
esac

current=$(cat "$home/current" 2>/dev/null || true)
[ -n "$target" ] || target=$(newest_passing || true)
if [ -z "$target" ]; then
    log "GitHub reported no passing CI run on main; nothing to do"
    exit 0
fi

if [ "$mode" = check ]; then
    echo "newest passing commit: $target"
    echo "live commit:           ${current:-none}"
    if have_images "$target"; then echo "images: available"; else echo "images: not available"; fi
    exit 0
fi

[ "$target" = "$current" ] && exit 0

# One deploy at a time; a timer firing during a slow pull just waits for the next tick.
if ! mkdir "$home/lock" 2>/dev/null; then
    echo "Another deploy is running ($home/lock)." >&2
    exit 0
fi
trap 'rmdir "$home/lock" 2>/dev/null || true' EXIT

if ! fetch_release "$target"; then
    log "could not fetch the Compose files for $target; still on ${current:-nothing}"
    exit 1
fi
if ! have_images "$target"; then
    log "images for $target are not available (not published yet, or the packages are private); still on ${current:-nothing}"
    exit 1
fi

log "deploying $target (live: ${current:-nothing})"
if start "$target" && healthy; then
    [ -n "$current" ] && echo "$current" > "$home/previous"
    echo "$target" > "$home/current"
    # The release's own copy of this script runs from the next tick (the timer swaps it in first;
    # a running shell script must never be overwritten).
    cp "$home/releases/$target/deploy.sh" "$home/deploy.sh.next"
    log "live: $target"
    prune "$target" "$current"
    exit 0
fi

if [ -n "$current" ]; then
    log "$target failed to start or its checks failed; rolling back to $current"
    if start "$current" && healthy; then
        log "rolled back: $current is live"
    else
        log "ROLLBACK FAILED: $current did not come back; the site needs attention"
    fi
else
    log "$target failed to start or its checks failed, and there is no earlier release to return to"
fi
exit 1
