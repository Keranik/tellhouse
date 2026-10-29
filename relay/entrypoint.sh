#!/bin/sh
# Tellhouse sealed relay entrypoint.
#
# Two seed modes, chosen by TELLHOUSE_DEPLOY_MODE:
#
# demo (the default): deterministic reseed from the baked demo
# registry. On an ephemeral disk the registry file vanishes
# with the instance, so a fresh boot re-seeds the demo registry
# from the image; a present registry is never overwritten. If
# the config file is present at boot, it is used as-is and
# operator state wins over reseeding.
#
# fresh (set by the deployment blueprint in render.yaml): the
# baked demo registry is NOT used, so none of the published
# demo token hashes can connect anything here. The registry is
# built from operator-supplied credential hashes alone:
# RELAY_NODE_TOKEN_SHA256, RELAY_LISA_TOKEN_SHA256, and
# RELAY_DANIEL_TOKEN_SHA256 (hex SHA-256 of each connection's
# credential; see DEPLOY.md, "Deploying fresh"). A hash that is
# not set yet simply leaves that connection out; the operator
# sets the values and restarts the service as the deployment
# steps complete. A present registry is never overwritten in
# either mode.
set -eu

RELAY_CONFIG="${RELAY_CONFIG:-/tmp/relay/config.json}"
RELAY_RUNTIME_DIR="${RELAY_RUNTIME_DIR:-/tmp/relay/runtime}"

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DEMO_SEED="$SCRIPT_DIR/demo-seed/relay-config.json"
RELAY_PROGRAM="$SCRIPT_DIR/src/sealed_relay.py"

mkdir -p "$(dirname -- "$RELAY_CONFIG")"
mkdir -p "$RELAY_RUNTIME_DIR"

if [ ! -f "$RELAY_CONFIG" ]; then
    if [ "${TELLHOUSE_DEPLOY_MODE:-demo}" = "fresh" ]; then
        python3 - "$RELAY_CONFIG" <<'PYEOF'
import json
import os
import sys

conns = []
for cid, role, env_name in (
    ("node-conn-01", "node", "RELAY_NODE_TOKEN_SHA256"),
    ("conn-lisa-01", "member", "RELAY_LISA_TOKEN_SHA256"),
    ("conn-daniel-01", "member", "RELAY_DANIEL_TOKEN_SHA256"),
):
    value = os.environ.get(env_name, "").strip()
    if value:
        conns.append({
            "connection_id": cid,
            "household_endpoint": "hh-demo-01",
            "standing": "good",
            "role": role,
            "token_sha256": value,
        })
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump({"connections": conns}, fh, indent=2)
print("relay entrypoint: fresh mode registry written with "
      + str(len(conns)) + " operator-supplied connection(s); "
      + "the baked demo registry was not used")
PYEOF
    else
        if [ ! -f "$DEMO_SEED" ]; then
            echo "entrypoint: demo seed registry not found at $DEMO_SEED" >&2
            exit 1
        fi
        cp "$DEMO_SEED" "$RELAY_CONFIG"
    fi
fi

exec python3 "$RELAY_PROGRAM" --config "$RELAY_CONFIG" --runtime-dir "$RELAY_RUNTIME_DIR" --host 0.0.0.0 --port "${PORT:-8790}"
