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
  "public.\"PlayerNameIndex\""
  "public.\"PlayerAliases\""
  "public.\"TrophyDefinitions\""
  "public.\"ClubCrest\""
  "public.\"GridTemplates\""
)
# Note: "PlayerNameIndex" and "ClubCrest" are placeholders for tables that
# don't exist yet (S-032, Tier 2 respectively) — their real DbSet/table
# names aren't confirmed until those entities are actually built. Every
# other entry above is verified directly against XGArcadeDbContext.cs's
# DbSet<T> property names (EF Core's default table-naming convention).
