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
