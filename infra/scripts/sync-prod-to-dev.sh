#!/usr/bin/env bash
# Syncs game/reference data from production to dev, one direction of the
# bidirectional pair described in ADR-0009. Use this when prod has game
# data (e.g. an admin correction, or newly-verified player data) that dev
# should pick up. The recommended day-to-day workflow is the other
# direction (build game data in dev, promote to prod via
# promote-dev-to-prod.sh) — this script exists for the "prod changed
# directly" case, not as the primary path.
#
# Per ADR-0006/ADR-0009 and REQ-804: this script must NEVER touch User,
# NotificationPreference, League, LeagueMembership, Guess, Round, or
# GridInstance/GridCell — see lib/game-data-tables.sh for the enforced
# allowlist and the reasoning behind it.
#
# Requires: PROD_DATABASE_URL and DEV_DATABASE_URL environment variables
# (Supabase connection strings), pg_dump/pg_restore available on PATH.
#
# Usage:
#   ./sync-prod-to-dev.sh          # runs the sync for real
#   ./sync-prod-to-dev.sh --dry-run  # lists what would be dumped, touches nothing

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/game-data-tables.sh
source "$SCRIPT_DIR/lib/game-data-tables.sh"

DRY_RUN=false
if [[ "${1:-}" == "--dry-run" ]]; then
  DRY_RUN=true
fi

if [[ -z "${PROD_DATABASE_URL:-}" || -z "${DEV_DATABASE_URL:-}" ]]; then
  echo "PROD_DATABASE_URL and DEV_DATABASE_URL must both be set." >&2
  exit 1
fi

echo "This will copy the following GAME/REFERENCE tables from PRODUCTION into DEV:"
printf '  %s\n' "${GAME_DATA_TABLES[@]}"
echo "User accounts, leagues, guesses, rounds, and all auth.* tables are never touched — see lib/game-data-tables.sh."

if [[ "$DRY_RUN" == true ]]; then
  echo ""
  echo "--dry-run: showing row counts that would be copied, touching nothing else."
  for t in "${GAME_DATA_TABLES[@]}"; do
    echo -n "  $t: "
    psql "$PROD_DATABASE_URL" -t -c "SELECT COUNT(*) FROM $t;" 2>/dev/null || echo "(unable to query — check connection)"
  done
  echo ""
  echo "Dry run complete. No data was copied. Run without --dry-run to actually sync."
  exit 0
fi

read -r -p "Type 'sync' to proceed: " CONFIRMATION
if [[ "$CONFIRMATION" != "sync" ]]; then
  echo "Aborted."
  exit 1
fi

DUMP_FILE="$(mktemp /tmp/xg-arcade-sync-XXXXXX.dump)"
trap 'rm -f "$DUMP_FILE"' EXIT

TABLE_ARGS=()
for t in "${GAME_DATA_TABLES[@]}"; do
  TABLE_ARGS+=("--table=$t")
done

echo "Dumping game/reference tables from production..."
pg_dump --format=custom --data-only "${TABLE_ARGS[@]}" \
  --dbname="$PROD_DATABASE_URL" --file="$DUMP_FILE"

echo "Restoring into dev (data only, existing rows truncated first)..."
for t in "${GAME_DATA_TABLES[@]}"; do
  psql "$DEV_DATABASE_URL" -c "TRUNCATE TABLE $t CASCADE;"
done
pg_restore --data-only --disable-triggers --dbname="$DEV_DATABASE_URL" "$DUMP_FILE"

echo "Sync complete. Synced tables: ${GAME_DATA_TABLES[*]}"
echo "Reminder: dev test users must be created via the test-data API (REQ-803), never synced from prod."
