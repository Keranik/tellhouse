#!/bin/bash
# the backup tool proof run: build, selftest, and (when present) the restore
# proof against a real seeded record database, when one is present. This script makes no network calls
# beyond the local NuGet cache restore that dotnet build performs.
set -u
export PATH="$HOME/.dotnet:$PATH"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/TellhouseBackup"
WORK="$(mktemp -d)"
echo "backup proof workdir: $WORK"

dotnet build "$PROJ" -v q --nologo || exit 1
DLL="$PROJ/bin/Debug/net10.0/TellhouseBackup.dll"

echo "--- selftest (fixture node record) ---"
dotnet "$DLL" selftest --workdir "$WORK/selftest" || exit 1

REAL_DB="$ROOT/src/HouseholdCoordinator/household-coordinator.db"
if [ -f "$REAL_DB" ]; then
  echo "--- restore proof against the real seeded record database ---"
  cp "$REAL_DB" "$WORK/real.db"
  OUT="$(dotnet "$DLL" backup --db "$WORK/real.db" --keystore "$WORK/ks/content-key.json" --out "$WORK/real.thbackup")" || exit 1
  echo "$OUT" | grep -v "RECOVERY PHRASE" | head -2
  PHRASE="$(echo "$OUT" | tail -1)"
  dotnet "$DLL" restore --backup "$WORK/real.thbackup" --phrase "$PHRASE" --dest-db "$WORK/fresh/node.db" || exit 1
  dotnet "$DLL" verify --before "$WORK/real.db" --after "$WORK/fresh/node.db" || exit 1
  echo "real-db restore proof: verify equal (see line above)"
else
  echo "real seeded database not present in this tree; fixture proof only"
fi
echo "BACKUP PROOF RUN COMPLETE"
