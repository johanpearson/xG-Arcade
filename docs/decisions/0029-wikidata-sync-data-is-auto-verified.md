# ADR-0029: Wikidata sync data starts verified; only the guess-time fallback stays reviewable

- **Status:** Superseded by ADR-0032 (2026-07-20) — the guess-time-fallback carve-out
  below is reversed; kept here for history, not deleted.
- **Date:** 2026-07-19
- **Related requirements:** REQ-502, REQ-503, REQ-211
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-05 (Games.XGGrid)

## Context

Every `PlayerData` row synced from Wikidata (`WikidataLookupService.PersistAttributeAsync`) was
persisted with `Confidence = "unverified"`, unconditionally, since S-006 — regardless of whether
the write came from a routine grid-generation cache-miss, an explicit `warm-player-cache` pass, or
REQ-211/ADR-0018's guess-time fallback. REQ-503's admin review list (`GET
/admin/player-data/unverified`, S-012) was designed around this: "admin reviews auto-fetched data
so the cache is quality-assured over time."

S-026 gave that endpoint its first real UI caller. At real data volume — accumulated across every
story since S-006 — the review queue had reached 52,782 rows, all sourced from Wikidata, with no
bulk action to clear it (REQ-503's own "approve → verified" action was never built, a gap already
noted in that requirement's status text). A manual, one-row-at-a-time review queue at that size is
not a workable admin tool; the premise it was built on doesn't match how this data actually
accumulates in practice.

## Decision

`Confidence` at write time now depends on *why* the Wikidata lookup ran, not just that it came from
Wikidata — a new `WikidataLookupOrigin` enum (`Sync` | `GuessTimeFallback`), passed explicitly by
every caller of `IWikidataLookupService.LookupAndPersistAsync`/`LookupAndPersistClubClubAsync`:

- **`Sync`** — a routine grid-generation cache-miss (`GridGameModule.GetMatchCountAsync`) or an
  explicit cache-warming pass (`PlayerCacheWarmingService`). Both are the same vetted per-category
  SPARQL intersection query Tier 0's "Wikidata-first" design already treats as ground truth
  (MVP-SCOPE.md) — persisted as `Confidence = "verified"`.
- **`GuessTimeFallback`** — REQ-211/ADR-0018's guess-time re-check
  (`GridGameModule.RefreshCellFromLiveLookupAsync`), triggered by one specific player's guess
  against a single already-generated cell, not the original vetted intersection. Kept
  `Confidence = "unverified"` so an admin can still spot-check this narrower, less-common case.

The existing 52,782-row backlog cannot be split into these two categories after the fact — no
`PlayerData` row records which code path created it (`Source` is always the literal `"wikidata"`
either way). A one-time CLI verb, `dotnet run -- verify-wikidata-player-data`
(`verify-wikidata-player-data.yml`, manual `workflow_dispatch` only, same shape as
`warm-player-cache.yml`), bulk-flips every currently-`unverified` wikidata-sourced row to
`verified` — matching the new default for the overwhelming majority of what actually created that
backlog, and safe to re-run (a second run finds nothing left to update).

REQ-502/503's actual review queue is deliberately left in place for a real third case — an
admin-reviewable channel fed by a *player's own suggestion* — but that channel doesn't exist yet
(there is no product surface today where a player submits a correction). Building it is out of
scope for this decision; when it exists, it becomes the primary source of `Confidence =
"unverified"` rows, not the sync path.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep everything unverified (status quo) | No code change | Review queue is already unworkable at real volume, and only grows | Doesn't address the actual problem |
| Auto-verify *all* Wikidata data, including the guess-time fallback | Simplest — one boolean, not an enum-by-origin | REQ-211's fallback is a narrower, less-vetted re-check triggered by an unusual guess; still worth a human being able to spot-check it | Throws away the one case where review still has real value |
| Add a manual "approve → verified" bulk action to the admin UI instead of changing the default | No backend sync-path change; matches REQ-503's original design | Doesn't fix the root cause — every future sync would still add to an ever-growing queue an admin has to manually clear forever | Treats the symptom, not the cause |
| Leave the historical 52k rows as-is, only fix new data | No data migration to write/run | The existing backlog is already unusable as a review queue, and no code path could ever distinguish which of it was fallback-origin vs. sync-origin later — waiting doesn't make that possible | The bulk-verify CLI verb is low-risk and one-time; deferring doesn't reduce the risk |

## Consequences

- Positive: the admin review queue becomes what REQ-503 actually intended — a small, browsable
  list of genuinely uncertain data — instead of an unbounded log of every Wikidata sync ever run.
- Positive: `WikidataLookupOrigin` makes the "why was this persisted" question explicit at every
  call site (`GetMatchCountAsync` → `Sync`, `RefreshCellFromLiveLookupAsync` → `GuessTimeFallback`,
  `PlayerCacheWarmingService` → `Sync`), rather than an implicit, undifferentiated default.
- Negative / trade-off accepted: the historical backlog's cleanup is an irreversible, all-or-
  nothing bulk write (no way to selectively re-flag only the "should have stayed unverified"
  subset, since that information was never captured) — accepted because the alternative (leaving
  it stuck at unusable) is worse, and re-running Wikidata syncs is idempotent, so nothing is lost.
- Follow-up: when a real user-suggestion channel exists (REQ-502/503's originally-intended third
  source), it should feed the same `Confidence = "unverified"` review queue — revisit this ADR's
  scope note above once that channel is designed.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
