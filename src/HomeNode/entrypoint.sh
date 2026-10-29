#!/bin/sh
# Tellhouse home node entrypoint.
#
# How a fresh boot works, stated plainly:
# - A fresh ephemeral disk boots by restoring the product's own
#   sealed demo snapshot through the product's own reset path
#   (TellhouseBackup demo-reset). Nothing here invents a second
#   seeding path.
# - The demo keystore and recovery phrase in the image are demo
#   material for the fictional Ellis household. They are baked in
#   on purpose so the demo node can open its own demo snapshot.
#   A real household's key, snapshot, and phrase would never
#   ship in an image.
# - An existing database or key file is never touched. The demo
#   key is copied in only when no key file exists yet, and the
#   snapshot is restored only when no database file exists yet.
#
# Fresh deploy mode (TELLHOUSE_DEPLOY_MODE=fresh, set by the
# deployment blueprint in render.yaml; never the default):
# - NONE of the baked demo material is used. No demo key is
#   copied and no demo snapshot is restored, so the published
#   demo recovery phrase and demo key open nothing here.
# - On the first boot the app generates its own node keypair
#   into the keystore and seeds the fictional day into a new
#   database. The entrypoint then stops it and takes this
#   deployment's first sealed backup with the product's own
#   backup tool. The recovery phrase the tool prints is this
#   deployment's phrase: it is captured into the keystore
#   directory only, and is never printed to the log.
# - Connections are not seeded in fresh mode. The stand-in
#   assistants join afterwards through the real door, using
#   invite codes the operator issues from the deployed node
#   (see DEPLOY.md, "Deploying fresh").
# - If a snapshot is present and the restore fails, this script
#   exits non-zero and the app does not start. A half-seeded node
#   must not start and pretend.
#
# Image layout:
#   /app                     HomeNode publish (HomeNode.dll here)
#   /app/backup              TellhouseBackup publish
#   /app/demo-seed/          demo-snapshot.thb
#                            demo-recovery-phrase.txt
#                            node-keys/node-content-key.txt
#
# Every path below is overridable by environment for local
# simulation:
#   DEMO_SEED_DIR   default /app/demo-seed
#   BACKUP_DLL      default /app/backup/TellhouseBackup.dll
#   APP_DLL         default /app/HomeNode.dll
#   HOME_NODE_KEYSTORE   default /tmp/node-keys
#   ConnectionStrings__HomeNode   SQLite connection string whose
#                            Data Source value names the database
#                            file; default /tmp/home-node.db

DEMO_SEED_DIR="${DEMO_SEED_DIR:-/app/demo-seed}"
BACKUP_DLL="${BACKUP_DLL:-/app/backup/TellhouseBackup.dll}"
APP_DLL="${APP_DLL:-/app/HomeNode.dll}"

# The keystore directory. Export it so the app reads its node
# content key from the same directory this script prepares.
HOME_NODE_KEYSTORE="${HOME_NODE_KEYSTORE:-/tmp/node-keys}"
KS_DIR="$HOME_NODE_KEYSTORE"
export HOME_NODE_KEYSTORE

# Parse the SQLite path from ConnectionStrings__HomeNode: strip
# the "Data Source=" prefix and keep only the value up to any
# semicolon. Default to /tmp/home-node.db when unset or empty.
CONN="${ConnectionStrings__HomeNode:-}"
if [ -z "$CONN" ]; then
    DB_PATH="/tmp/home-node.db"
else
    case "$CONN" in
        "Data Source="*)
            DB_PATH="${CONN#Data Source=}"
            ;;
        *)
            DB_PATH="$CONN"
            ;;
    esac
    DB_PATH="${DB_PATH%%;*}"
    if [ -z "$DB_PATH" ]; then
        DB_PATH="/tmp/home-node.db"
    fi
fi

DB_DIR="$(dirname "$DB_PATH")"
mkdir -p "$KS_DIR"
mkdir -p "$DB_DIR"

DEPLOY_MODE="${TELLHOUSE_DEPLOY_MODE:-demo}"

if [ "$DEPLOY_MODE" = "fresh" ]; then
    echo "entrypoint: fresh deploy mode: the baked demo key, snapshot, and phrase are NOT used"
    if [ ! -f "$DB_PATH" ] && [ ! -f "$KS_DIR/deployment-snapshot.thb" ]; then
        echo "entrypoint: first fresh boot: starting the app once so it generates its own keypair and seeds the record"
        # The seeding instance binds a throwaway loopback port,
        # never the service port: it exists only to generate the
        # keypair and seed the database, and it is stopped before
        # the backup. Nothing external may reach it, because the
        # node's door keeps each knock's credential verifier in
        # process memory; a knock served by this throwaway
        # instance would be unverifiable once it stops. The real
        # instance below is the first one anything can talk to.
        ASPNETCORE_URLS="http://127.0.0.1:0" dotnet "$APP_DLL" &
        APP_PID=$!
        # Seeding is done when the database stops growing. The
        # database runs in WAL mode, so the main file and its
        # WAL are summed; the node key file is not part of the
        # signal (the keypair is written lazily, and the real
        # instance below writes it at its own start if the
        # seeding instance has not).
        WAITED=0
        LAST_SIZE=-1
        STABLE=0
        while [ $WAITED -lt 180 ]; do
            if [ -f "$DB_PATH" ]; then
                SIZE=$(( $(stat -c %s "$DB_PATH" 2>/dev/null || echo 0) + $(stat -c %s "$DB_PATH-wal" 2>/dev/null || echo 0) ))
                if [ "$SIZE" = "$LAST_SIZE" ] && [ "$SIZE" -gt 0 ]; then
                    STABLE=$((STABLE + 1))
                    if [ $STABLE -ge 3 ]; then
                        break
                    fi
                else
                    STABLE=0
                fi
                LAST_SIZE=$SIZE
            fi
            sleep 2
            WAITED=$((WAITED + 2))
        done
        kill $APP_PID 2>/dev/null || true
        wait $APP_PID 2>/dev/null || true
        if [ ! -f "$DB_PATH" ]; then
            echo "entrypoint: the first fresh boot did not produce a database at $DB_PATH; refusing to start" >&2
            exit 1
        fi
        PHRASE_CAPTURE="$KS_DIR/.phrase-capture"
        if ! dotnet "$BACKUP_DLL" backup --db "$DB_PATH" --keystore "$KS_DIR/content-keystore.json" --out "$KS_DIR/deployment-snapshot.thb" > "$PHRASE_CAPTURE" 2>&1; then
            cat "$PHRASE_CAPTURE" >&2
            rm -f "$PHRASE_CAPTURE"
            echo "entrypoint: the deployment's first sealed backup failed; refusing to start" >&2
            exit 1
        fi
        awk 'NF { line = $0 } END { print line }' "$PHRASE_CAPTURE" > "$KS_DIR/deployment-recovery-phrase.txt"
        grep -vxF -f "$KS_DIR/deployment-recovery-phrase.txt" "$PHRASE_CAPTURE" || true
        rm -f "$PHRASE_CAPTURE"
        echo "entrypoint: this deployment's sealed backup is at $KS_DIR/deployment-snapshot.thb; its fresh recovery phrase is stored only in $KS_DIR/deployment-recovery-phrase.txt"
    fi
else
    # Copy the baked demo key in only when no key file exists yet.
    # Never overwrite an existing key file.
    if [ ! -f "$KS_DIR/node-content-key.txt" ] && [ -f "$DEMO_SEED_DIR/node-keys/node-content-key.txt" ]; then
        cp "$DEMO_SEED_DIR/node-keys/node-content-key.txt" "$KS_DIR/node-content-key.txt"
        echo "entrypoint: copied the demo node content key into $KS_DIR"
    fi

    # Restore the sealed demo snapshot only when no database file
    # exists yet. Never touch an existing database.
    if [ ! -f "$DB_PATH" ] && [ -f "$DEMO_SEED_DIR/demo-snapshot.thb" ]; then
        if [ ! -f "$DEMO_SEED_DIR/demo-recovery-phrase.txt" ]; then
            echo "entrypoint: demo snapshot is present but $DEMO_SEED_DIR/demo-recovery-phrase.txt is missing; refusing to start" >&2
            exit 1
        fi
        echo "entrypoint: no database at $DB_PATH; restoring the sealed demo snapshot through TellhouseBackup demo-reset"
        # The app is not running yet, so this restore cannot swap a
        # file under a live node. A previous database at this path
        # can still have left journal sidecars behind (a -wal and
        # -shm pair from an unclean stop); remove them here as well
        # as in the restore tool, so no stale journal can be
        # replayed against the restored file on the first open.
        rm -f "$DB_PATH-wal" "$DB_PATH-shm" "$DB_PATH-journal"
        if ! dotnet "$BACKUP_DLL" demo-reset --backup "$DEMO_SEED_DIR/demo-snapshot.thb" --phrase "$(cat "$DEMO_SEED_DIR/demo-recovery-phrase.txt")" --dest-db "$DB_PATH"; then
            echo "entrypoint: demo snapshot restore failed; refusing to start a half-seeded node" >&2
            exit 1
        fi
    fi
fi

exec dotnet "$APP_DLL"
