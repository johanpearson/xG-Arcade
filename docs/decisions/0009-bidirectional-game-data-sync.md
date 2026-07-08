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
