#!/bin/sh
# Entrypoint for the Daniel reference assistant container.
#
# Seed rule: the demo seed is the post-ceremony store of the demo
# seed ceremony (demo keys for a fictional member, in the repo on
# purpose). It is copied in only when the store has no state.json
# yet. A store that exists is the assistant's own and is never
# reseeded.
#
# Fresh deploy mode (TELLHOUSE_DEPLOY_MODE=fresh, set by the
# deployment blueprint in render.yaml): the baked store is never
# copied. The assistant starts unjoined with the keypair it
# generates itself and joins through the real door with an
# invite the operator supplies by environment (STANDIN_AUTO_JOIN
# and STANDIN_INVITE_CODE; see DEPLOY.md, "Deploying fresh").
set -eu

STORE="${STANDIN_STORE:-/tmp/store}"
APP_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SEED_DIR="$APP_DIR/demo-seed/store"

mkdir -p "$STORE"

if [ "${TELLHOUSE_DEPLOY_MODE:-demo}" = "fresh" ]; then
    echo "entrypoint: fresh deploy mode: the baked demo store is NOT copied; this assistant starts unjoined with its own generated keypair"
elif [ ! -f "$STORE/state.json" ] && [ -d "$SEED_DIR" ]; then
    if [ -f "$SEED_DIR/keys.json" ]; then
        cp "$SEED_DIR/keys.json" "$STORE/keys.json"
    fi
    if [ -f "$SEED_DIR/state.json" ]; then
        cp "$SEED_DIR/state.json" "$STORE/state.json"
    fi
fi

export STANDIN_STORE="$STORE"
exec dotnet "$APP_DIR/DanielAssistant.dll"
