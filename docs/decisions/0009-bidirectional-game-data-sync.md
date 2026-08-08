# ADR-0009: Bidirectional game-data sync (dev↔prod), never results or customer data

- **Status:** Accepted — supersedes ADR-0006's "one-way only" clause specifically; the rest of ADR-0006 (two-project split, allowlist approach) is unchanged
- **Date:** 2026-07-07
- **Related requirements:** REQ-804 (revised), REQ-805 (new)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-10 (Data.PlayerNameIndex)

## Context

ADR-0006 established a one-way-only sync (prod → dev) as a safety measure.
In practice, the intended workflow is closer to the opposite: game/reference
data (football players, clubs, trophies, grid templates) gets built up and
curated in dev — where the test-data API and admin review tools are safe to
experiment with — and then promoted to prod once it's verified. The
one-way restriction made this backwards: it allowed the low-value direction
(refreshing dev from prod) but not the high-value one (promoting curated
work from dev to prod).

Separately, it's worth being explicit about a distinction that was
previously only implicit in the table allowlist: "game data" (data ABOUT
footballers/clubs/trophies) is categorically different from both
**results** (`Guess`, `Round`, `GridInstance`, `GridCell` — actual gameplay
activity, inherently specific to each environment's own rounds) and
**customer/player data** (`User`, `NotificationPreference`, `League`,
`LeagueMembership` — real people's accounts and activity). Only the first
category is ever eligible to sync, in either direction.

## Decision

- Sync becomes **bidirectional**, but only for the game/reference-data
  allowlist (`infra/scripts/lib/game-data-tables.sh`) — the same
  allowlist for both directions, defined once, sourced by both scripts, so
  the two directions can never drift apart on what's safe to move.
- **`promote-dev-to-prod.sh`** (new): the **recommended, primary
  direction** for day-to-day work. Build and curate game data in dev,
  promote it to prod when ready.
- **`sync-prod-to-dev.sh`** (existing, kept): the fallback direction, for
  when prod's game data changed directly (an urgent live correction, say)
  and dev needs to catch up. Not the primary workflow.
- **Results and customer/player data are never synced, in either
  direction, under any circumstance.** This isn't just an allowlist
  omission — it's the categorical rule the allowlist exists to enforce.
  `Guess`/`Round`/`GridInstance`/`GridCell` are excluded because they're
  inherently per-environment (dev's test rounds are not prod's real
  rounds, and syncing them either direction is meaningless, not just
  risky). `User`/`NotificationPreference`/`League`/`LeagueMembership` are
  excluded because they're real people's data — see ADR-0006 for the
  original reasoning, which still holds.
- Both scripts require the same explicit confirmation-to-proceed pattern
  as before, but the prod-writing direction (`promote-dev-to-prod.sh`)
  requires a longer, more explicit confirmation phrase ("promote to prod"
  vs. "sync") as a deliberate extra friction point given it writes to what
  real users are actively playing against.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep one-way only (status quo) | Simpler, matches original safety reasoning | Doesn't match the actual intended workflow — building data in dev and being unable to promote it defeats the point of building it there | The whole reason to curate in dev is to eventually ship it |
| Allow full bidirectional sync of everything, including results/users | Simplest mental model | Exactly the risk ADR-0006 existed to prevent — real user data crossing environments | Never acceptable regardless of workflow convenience |
| One combined script with a `--direction` flag | Less code duplication | A single wrong flag value could sync the wrong direction; two distinctly-named scripts/workflows make the direction unmistakable at the point of use | Clarity at the moment of running a prod-writing command matters more than avoiding minor duplication |

## Consequences

- Positive: the actual recommended workflow (curate in dev, promote to
  prod) is now a real, safe, first-class operation instead of something
  the tooling worked against
- Negative / trade-offs accepted: two scripts to maintain instead of one,
  though the shared allowlist file keeps them from diverging on the part
  that actually matters (what's safe to move)
- Follow-up: if a new game-content table is ever added to the schema, it
  must be a deliberate decision to add it to
  `lib/game-data-tables.sh` — the allowlist doesn't grow automatically,
  and that's the point

## For AI agents

Never add `Guess`, `Round`, `GridInstance`, `GridCell`, `User`,
`NotificationPreference`, `League`, or `LeagueMembership` to
`lib/game-data-tables.sh`, regardless of which direction a task seems to
need it for. If a task seems to require syncing any of these, stop and
flag it — that's exactly the case this ADR (and ADR-0006 before it) exists
to prevent. The two scripts must always source the same shared allowlist
file — never let one define its own inline copy.

## Addendum, 2026-08-08: `PlayerCareerStint` was missing from the allowlist

Found by an `architecture-reviewer` pass evaluating a proposed alternative
(a genuinely shared dev/prod database for reference data — see the
in-progress ADR on that topic for the full evaluation, which recommended
against sharing `Player`-family tables specifically). `PlayerCareerStint`
(ADR-0042, populated by ADR-0054/ADR-0055's fetch/prefetch services)
postdated this allowlist's most recent addition and was simply never added
— a real gap, not a deliberate exclusion: dev and prod have had no sync
path for this table at all since it was introduced. Added to
`lib/game-data-tables.sh` as `"public.\"PlayerCareerStints\""`, same
allowlist, same two scripts, no other change to this ADR's decision.

## Addendum, 2026-08-08: `TRUNCATE ... CASCADE` was silently wiping non-allowlisted operational tables — fixed; plus a scheduled dry-run check

Found by an `architecture-reviewer` pass (the same session that evaluated
and rejected the shared-DB alternative referenced in the addendum above).
Both scripts' restore step ran `TRUNCATE TABLE $t CASCADE;` per allowlisted
table before restoring. In Postgres, `TRUNCATE ... CASCADE` doesn't just
cascade to rows — it truncates every OTHER table that has a foreign key
referencing the truncated table, in full, regardless of whether that table
is in `GAME_DATA_TABLES`. Truncating `"Players"` this way also silently
wiped `"PathPuzzles"` and `"PathCycleTargetUsages"` — both xG Path's
round/cycle-scoped operational data, the same category as
`Round`/`GridInstance`/`GridCell`, which this ADR says must never be
touched by either script "regardless of direction... under any
circumstance." Verified directly against `XGArcadeDbContext.cs`'s
`OnModelCreating`: those two are the only foreign keys today from a table
outside `GAME_DATA_TABLES` into one inside it (`PathPuzzle.TargetPlayerId`
and `PathCycleTargetUsage.PlayerId`, both referencing `Player`). It had
never fired for real only because prod doesn't exist yet.

**Fix** (`infra/scripts/lib/game-data-tables.sh`, new
`truncate_game_data_tables_safely`/`restore_external_foreign_keys`
functions, shared by both scripts): before truncating, find every FK
constraint defined on a table outside `GAME_DATA_TABLES` that references a
table inside it (queries `pg_constraint` at runtime, so this isn't
hardcoded to today's two known cases — it also covers any future FK added
without anyone remembering to update this file), drop just those
constraints, `TRUNCATE` every `GAME_DATA_TABLES` member together in a
single statement with no `CASCADE` keyword at all, restore, then re-add
the dropped constraints. `SET session_replication_role = replica` (the
standard technique for this kind of full data-only reload) was considered
first but verified, against a real local Postgres 16 instance rather than
assumed, to not solve this specific problem: `TRUNCATE`'s "cannot truncate
a table referenced by a foreign key" pre-check and `CASCADE`'s table
expansion are both evaluated statically, before any trigger would fire, so
`session_replication_role` doesn't affect either one. Removing `CASCADE`
entirely (rather than, say, scoping it more narrowly) is deliberate
defense-in-depth: if some future schema change adds another external FK
this discovery query somehow misses, a plain `TRUNCATE` now fails loudly
instead of silently wiping unexpected data.

One known residual limitation, inherent to a full-replace sync strategy
and not something this fix introduces: re-adding a dropped FK constraint
validates it against the now-restored data, so if the target
environment's own live operational rows (e.g. an in-progress prod round's
`PathPuzzle.TargetPlayerId`) reference a player that the incoming sync
doesn't include, the constraint re-add fails and the script exits non-zero
(via `set -euo pipefail`) with the affected data already fully synced but
the FK left unenforced until someone investigates. This is the correct
"fail loud" behavior for what would otherwise be a silent orphaned
reference — not a bug in the fix — but it means a promote/sync can still
require manual follow-up in that specific edge case. Verified this
failure mode directly (a deliberately orphaned FK) as well as the
successful path (steady-state promote where dev's player set is a
superset of what prod's operational tables reference — the realistic case
after a first promote has already happened).

**Also added**: `promote-dev-to-prod-dry-run.yml`, a weekly scheduled
workflow that runs `promote-dev-to-prod.sh --dry-run` and writes the
result to the GitHub Actions job summary, so drift between dev and prod's
game/reference data is visible without a human remembering to check. It
never writes to prod and adds no non-interactive flag to the real promote
path — that was explicitly considered and rejected; the real promote still
requires a human running the script by hand and typing the confirmation
phrase. It exits cleanly (not a failing red run) when
`PROD_DATABASE_CONNECTION_STRING` isn't set, since prod doesn't exist yet
(Tier 1, `MVP-SCOPE.md`). Both `--dry-run` modes (`promote-dev-to-prod.sh`
and `sync-prod-to-dev.sh`) were also extended to show both sides' row
counts per table (previously only the source side's), so a dry run is an
actual diff a reviewer can read, not just one side's numbers — this
applies to manual `--dry-run` usage too, not just the new scheduled job.

No new ADR: this is a bug fix (restoring the guarantee this ADR already
states) plus a scheduling/visibility refinement of the existing
`--dry-run` flag, not a new structural decision.
