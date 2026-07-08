#!/usr/bin/env bash
# Promotes game/reference data built up in dev into production. This is
# the RECOMMENDED day-to-day direction (ADR-0009): build/curate game data
# in dev — new player attributes, admin corrections, grid templates — then
# promote the verified result to prod, rather than editing prod directly.
#
# Same allowlist as sync-prod-to-dev.sh (lib/game-data-tables.sh), same
# exclusions: never User, NotificationPreference, League, LeagueMembership,
# Guess, Round, GridInstance/GridCell, or any auth.* table, regardless of
# direction.
#
# This writes to PRODUCTION. Treat it with more caution than the reverse
# direction — the confirmation phrase is deliberately more explicit about
# what's about to happen.
#
# Requires: PROD_DATABASE_URL and DEV_DATABASE_URL environment variables
# (Supabase connection strings), pg_dump/pg_restore available on PATH.
#
# Usage:
#   ./promote-dev-to-prod.sh          # runs the promotion for real
#   ./promote-dev-to-prod.sh --dry-run  # lists what would be dumped, touches nothing

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

echo "This will copy the following GAME/REFERENCE tables from DEV into PRODUCTION:"
printf '  %s\n' "${GAME_DATA_TABLES[@]}"
echo "User accounts, leagues, guesses, rounds, and all auth.* tables are never touched — see lib/game-data-tables.sh."
echo ""
echo "This writes to PRODUCTION. Anyone playing right now sees the result immediately."

if [[ "$DRY_RUN" == true ]]; then
  echo ""
  echo "--dry-run: showing row counts that would be promoted, touching nothing else."
  for t in "${GAME_DATA_TABLES[@]}"; do
    echo -n "  $t: "
    psql "$DEV_DATABASE_URL" -t -c "SELECT COUNT(*) FROM $t;" 2>/dev/null || echo "(unable to query — check connection)"
  done
  echo ""
  echo "Dry run complete. No data was copied. Run without --dry-run to actually promote."
  exit 0
fi

read -r -p "Type 'promote to prod' to proceed: " CONFIRMATION
if [[ "$CONFIRMATION" != "promote to prod" ]]; then
  echo "Aborted."
  exit 1
fi

DUMP_FILE="$(mktemp /tmp/xg-arcade-promote-XXXXXX.dump)"
trap 'rm -f "$DUMP_FILE"' EXIT

TABLE_ARGS=()
for t in "${GAME_DATA_TABLES[@]}"; do
  TABLE_ARGS+=("--table=$t")
done

echo "Dumping game/reference tables from dev..."
pg_dump --format=custom --data-only "${TABLE_ARGS[@]}" \
  --dbname="$DEV_DATABASE_URL" --file="$DUMP_FILE"

echo "Restoring into production (data only, existing rows truncated first)..."
for t in "${GAME_DATA_TABLES[@]}"; do
  psql "$PROD_DATABASE_URL" -c "TRUNCATE TABLE $t CASCADE;"
done
pg_restore --data-only --disable-triggers --dbname="$PROD_DATABASE_URL" "$DUMP_FILE"

echo "Promotion complete. Synced tables: ${GAME_DATA_TABLES[*]}"
