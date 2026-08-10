#!/usr/bin/env bash
# Single source of truth for which tables are ever allowed to move between
# prod and dev, in either direction. Sourced by both sync-prod-to-dev.sh
# and promote-dev-to-prod.sh — defined once so the two scripts can never
# drift apart on what's safe to sync.
#
# Per ADR-0009 (superseding ADR-0006's one-way-only clause): only football/
# game REFERENCE data belongs here — data ABOUT footballers, clubs, and
# trophies. Never:
#   - Results/gameplay activity: Guess, Round, GridInstance, GridCell —
#     these are inherently environment-specific (dev's test rounds and
#     prod's real rounds are never the same rounds) and are never synced,
#     regardless of direction.
#   - Customer/player accounts: User, NotificationPreference, League,
#     LeagueMembership — real people's data, never synced either way.
#     ("Player" in this codebase means a footballer in the game content,
#     not a person playing the game — see requirements-document.md §2 for
#     the terminology this allowlist deliberately follows.)
#
# Adding a table here is a deliberate decision, not a default — this is an
# allowlist, not a denylist, specifically so a new table added elsewhere in
# the schema is excluded until someone consciously puts it here.

GAME_DATA_TABLES=(
  "public.\"Players\""
  "public.\"PlayerData\""
  "public.\"PlayerOverrides\""
  "public.\"PlayerAttributes\""
  "public.\"PlayerNameIndexEntries\""
  "public.\"PlayerNameIndexWords\""
  "public.\"PlayerAliases\""
  "public.\"PlayerCareerStints\""
  "public.\"TrophyDefinitions\""
  "public.\"ClubCrest\""
  "public.\"GridTemplates\""
)
# Note: S-032 built PlayerNameIndex (COMP-10, ADR-0007) — corrected here from
# the earlier placeholder entry "PlayerNameIndex" to the real table name,
# "PlayerNameIndexEntries" (XGArcadeDbContext's DbSet<PlayerNameIndex>
# property name, EF Core's default table-naming convention). "ClubCrest" is
# still a placeholder for a table that doesn't exist yet (Tier 2) — its real
# DbSet/table name isn't confirmed until that entity is actually built. Every
# other entry above is verified directly against XGArcadeDbContext.cs's
# DbSet<T> property names.
#
# "PlayerNameIndexWords" added 2026-07-26 (REQ-208's correction, ADR-0044):
# COMP-10's own per-word decomposition of PlayerNameIndexEntries.NormalizedName
# — same bulk-imported reference-data character as PlayerNameIndexEntries
# itself, and must travel with it so a synced environment never ends up with
# entries but no matching word rows (or vice versa).
#
# "PlayerCareerStints" added 2026-08-08 (gap found during an architecture
# review of a proposed shared-DB alternative to this allowlist): ADR-0042's
# PlayerCareerStint table postdates this file's most recent addition before
# today and was simply never added — a real gap, not a deliberate exclusion.
# It's exactly the kind of fetched/grown game-reference data (Wikidata career
# history, per ADR-0042/0054/0055) this allowlist exists to carry; unlike
# ClubDefinition/CountryDefinition (hand-curated, seeded identically in every
# environment from committed source via ReferenceDataSeeder, never needing a
# sync path at all), PlayerCareerStint genuinely accumulates independently
# per environment over time and drifts without this entry.

# --- FK-safety helpers for promote-dev-to-prod.sh / sync-prod-to-dev.sh's
# truncate+restore step ---
#
# Bug fixed 2026-08-08: both scripts used to run `TRUNCATE TABLE $t CASCADE;`
# per table in GAME_DATA_TABLES before restoring. In Postgres, TRUNCATE ...
# CASCADE doesn't just cascade to rows — it truncates every OTHER table that
# has a foreign key referencing the truncated table, in full, regardless of
# whether that table is in this allowlist. Truncating "Players" this way
# also silently wiped "PathPuzzles" and "PathCycleTargetUsages" — both
# round/cycle-scoped operational data, the same category as
# Round/GridInstance/GridCell, which ADR-0009 says must never be touched by
# either script "regardless of direction... under any circumstance."
#
# Verified directly against XGArcadeDbContext.cs's OnModelCreating
# (2026-08-08): those two are the only foreign keys from a table OUTSIDE
# this allowlist into any table INSIDE it, today. The fix below doesn't
# hardcode that fact, though — it queries pg_constraint at runtime, so it
# also covers any future FK added without anyone remembering to check here.
#
# On the fix mechanism itself: `SET session_replication_role = replica;` was
# considered first (it's the standard technique for a full data-only reload
# because it disables trigger-based FK enforcement for the session) but was
# verified, against a real local Postgres 16 instance, to NOT solve this
# specific problem — TRUNCATE's "cannot truncate a table referenced by a
# foreign key" pre-check and CASCADE's table-expansion are both static/DDL
# behaviors evaluated before any trigger would fire, not something
# session_replication_role affects. (Confirmed by direct test: wrapping
# `TRUNCATE ... CASCADE` in a replica-role session still cascaded to the
# referencing table exactly as without it.)
#
# What does work, also verified against a real Postgres 16 instance: (1)
# drop the specific external FK constraints found by the query below, (2)
# TRUNCATE every GAME_DATA_TABLES member together in a single statement —
# Postgres allows a plain, non-CASCADE TRUNCATE across a set of tables as
# long as every table that FK-references any table in the set is ALSO
# included in that same statement, which is true once step (1) removes the
# only external references — (3) restore, (4) re-add the dropped
# constraints. Deliberately no CASCADE keyword anywhere in this file any
# more: if some future schema change adds another external FK this
# discovery query somehow misses, a plain TRUNCATE now fails loudly instead
# of silently wiping unexpected data — fail closed, not fail silent.

# Builds the `(schema, table)` VALUES list the FK-discovery queries below
# join against, from GAME_DATA_TABLES's `schema."Table"` entries.
_gdt_allowlist_values_sql() {
  local values="" t schema rest table
  for t in "${GAME_DATA_TABLES[@]}"; do
    schema="${t%%.*}"
    rest="${t#*.}"
    table="${rest//\"/}"
    if [[ -z "$values" ]]; then
      values="('$schema','$table')"
    else
      values="$values, ('$schema','$table')"
    fi
  done
  printf '%s' "$values"
}

# Finds every FK constraint defined on a table OUTSIDE GAME_DATA_TABLES that
# references a table INSIDE it. $1 selects which half of the statement pair
# to build: "drop" or "add" (the add half uses pg_get_constraintdef, so it
# only works while the constraint still exists — callers must capture it
# before dropping).
_gdt_external_fk_query() {
  local mode="$1"
  local values_sql
  values_sql="$(_gdt_allowlist_values_sql)"
  local select_expr
  if [[ "$mode" == "drop" ]]; then
    select_expr="format('ALTER TABLE %I.%I DROP CONSTRAINT %I;', ccu_ns.nspname, ccu_tbl.relname, con.conname)"
  else
    select_expr="format('ALTER TABLE %I.%I ADD CONSTRAINT %I %s;', ccu_ns.nspname, ccu_tbl.relname, con.conname, pg_get_constraintdef(con.oid))"
  fi
  cat <<SQL
WITH allowlist(schema_name, table_name) AS (VALUES ${values_sql})
SELECT ${select_expr}
FROM pg_constraint con
JOIN pg_class ccu_tbl ON ccu_tbl.oid = con.conrelid
JOIN pg_namespace ccu_ns ON ccu_ns.oid = ccu_tbl.relnamespace
JOIN pg_class ref_tbl ON ref_tbl.oid = con.confrelid
JOIN pg_namespace ref_ns ON ref_ns.oid = ref_tbl.relnamespace
WHERE con.contype = 'f'
  AND EXISTS (SELECT 1 FROM allowlist a WHERE a.schema_name = ref_ns.nspname AND a.table_name = ref_tbl.relname)
  AND NOT EXISTS (SELECT 1 FROM allowlist a WHERE a.schema_name = ccu_ns.nspname AND a.table_name = ccu_tbl.relname);
SQL
}

# Drops any FK constraint from outside GAME_DATA_TABLES that references a
# table inside it, then TRUNCATEs every GAME_DATA_TABLES member together in
# one statement (no CASCADE — see the header comment above for why that's
# both necessary and safe). Writes the matching ADD CONSTRAINT statements to
# $restore_file so restore_external_foreign_keys can put them back once the
# caller's pg_restore step has repopulated the tables.
truncate_game_data_tables_safely() {
  local db_url="$1"
  local restore_file="$2"

  psql "$db_url" -t -A -c "$(_gdt_external_fk_query add)" >"$restore_file"

  local drop_stmts
  drop_stmts="$(psql "$db_url" -t -A -c "$(_gdt_external_fk_query drop)")"

  local truncate_list="" t
  for t in "${GAME_DATA_TABLES[@]}"; do
    if [[ -z "$truncate_list" ]]; then
      truncate_list="$t"
    else
      truncate_list="$truncate_list, $t"
    fi
  done

  {
    echo "BEGIN;"
    [[ -n "$drop_stmts" ]] && echo "$drop_stmts"
    echo "TRUNCATE TABLE $truncate_list;"
    echo "COMMIT;"
  } | psql "$db_url" -v ON_ERROR_STOP=1
}

# Re-adds the FK constraints truncate_game_data_tables_safely dropped, after
# the caller's pg_restore has repopulated GAME_DATA_TABLES. If this fails,
# it means the freshly-synced data no longer contains a row some *unsynced*
# operational table (e.g. a live xG Path round's PathPuzzle.TargetPlayerId)
# still points at — a genuine data problem in its own right, not something
# to paper over, so this deliberately fails loud (propagates via the
# caller's `set -euo pipefail`) rather than silently leaving the constraint
# unenforced.
restore_external_foreign_keys() {
  local db_url="$1"
  local restore_file="$2"

  if [[ -s "$restore_file" ]]; then
    psql "$db_url" -v ON_ERROR_STOP=1 -f "$restore_file"
  fi
}

# Prints a per-table row-count comparison for --dry-run. Shared by both
# scripts (only which DB is "source" vs "target" swaps) so the two
# directions can never drift on what a dry run actually shows.
print_dry_run_row_counts() {
  local source_url="$1" source_label="$2" target_url="$3" target_label="$4"
  local t source_count target_count

  printf '  %-32s %14s %14s\n' "table" "$source_label" "$target_label"
  for t in "${GAME_DATA_TABLES[@]}"; do
    source_count="$(psql "$source_url" -t -A -c "SELECT COUNT(*) FROM $t;" 2>/dev/null)"
    target_count="$(psql "$target_url" -t -A -c "SELECT COUNT(*) FROM $t;" 2>/dev/null)"
    printf '  %-32s %14s %14s\n' "$t" "${source_count:-?}" "${target_count:-?}"
  done
}
