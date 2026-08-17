# ADR-0073: xG Path adds its own `BirthYear >= 1975` eligibility floor, additive to REQ-112's shared 1939 pool floor

- **Status:** Accepted
- **Date:** 2026-08-17
- **Related requirements:** REQ-1201
- **Related components:** COMP-11 (Games.XGPath), COMP-06 (Data.PlayerStore)
- **Related decisions:** ADR-0045 (superseded on this one point only — see
  below), ADR-0025 (REQ-112's shared 1939 pool floor), ADR-0056 (familiarity
  filter ordering precedent), ADR-0070 (fail-closed precedent this ADR
  follows)

## Context

Epic 12 (docs/backlog.md) is a broader review of xG Path's target-player
eligibility. Its first story, S-137, adds a `Player.BirthYear >= 1975`
floor to `XGPathGameModule`'s eligibility pipeline, on top of the three
existing structural checks in `IsEligible` (≥3 stint rows, chronological
order determinable, ≥1 qualifying seeded-club stint — ADR-0045/ADR-0047).

`Player.BirthYear` already exists and is populated (REQ-1207/S-082, via
the now-removed-workflow-but-still-live backfill service — see S-132),
so this needs no new data pipeline. Two things needed a decision:

1. **Where the new floor lives.** xG Grid's own player pool already has a
   birth-year floor — REQ-112's `>= 1939`, enforced far upstream at
   Wikidata SPARQL query time in `WikidataClient`
   (`BuildCountryClubIntersectionQuery`/`BuildClubClubIntersectionQuery`/
   `BuildPlayerPoolBirthYearQuery`, ADR-0025). That floor is shared
   infrastructure: every `Player`/`PlayerCareerStint` row in the system,
   regardless of which game eventually uses it, already satisfies it by
   construction. xG Path's new `>= 1975` requirement is much narrower and
   is not something every consumer of `Player` data should be forced to
   accept — raising the *shared* floor to 1975 would also narrow xG
   Grid's own player pool, which is out of scope for this story (Epic
   12's intro paragraph makes the same point about why the existing 1939
   floor "cannot be changed there without also narrowing xG Grid's pool").
2. **What to do about a candidate with `BirthYear == null`.** Include it
   (benefit of the doubt) or exclude it (fail closed)?

## Decision

1. **The 1975 floor lives as an xG-Path-only, additive, runtime check in
   `XGPathGameModule.GetEligiblePlayerIdsAsync`** — not as a change to
   `WikidataClient`'s shared SPARQL query-building, and not inside
   `IsEligible`/`PathCareerStintFilter`. It runs as a `Player`-level
   check, using `IPlayerRepository.GetPlayersByIdsAsync` to bulk-fetch
   `BirthYear` for exactly the set of candidates that already passed the
   three existing structural checks — mirroring ADR-0056's own "the
   familiarity filter only sees structurally-eligible candidates"
   ordering precedent, extended to this new check: a candidate excluded
   by the `BirthYear` floor is never even offered to
   `IPlayerFamiliarityService.FilterFamiliarAsync`, avoiding a wasted
   familiarity-check call on a candidate that's already out.

   It is a `Player`-level check, not a `PlayerCareerStint`-level one,
   because `BirthYear` is a fact recorded once per player
   (`Player.BirthYear`), not per career-stint row — `PlayerCareerStint`
   has no `BirthYear` field and no natural way to carry one, so there is
   no clean way to fold this into `PathCareerStintFilter`'s stint-level
   filtering the way the national-team exclusion is folded in there.

2. **`BirthYear == null` is excluded, fail closed — matching this
   codebase's established convention (ADR-0070, REQ-211's fallback
   behavior).** xG Path cannot verify that a candidate with no recorded
   `BirthYear` meets the new floor; silently admitting them anyway would
   be exactly the "can't verify it, so treat it as passing" mistake
   ADR-0070 and REQ-211's own fallback design both deliberately avoid.
   The check reads as an explicit `HasValue` guard followed by the
   comparison, not a bare `BirthYear >= 1975` (which would also evaluate
   to `false` for `null` and coincidentally produce the same excluded
   outcome) — written this way so the null case is visibly a deliberate
   decision in the code, not an accident of nullable-int comparison
   semantics that a future refactor could quietly invert.

   S-141 is filed as the explicit follow-up to sweep remaining
   null-`BirthYear` rows, so this exclusion shrinks the pool as little as
   possible over time — deliberately not solved in this same story.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Raise REQ-112's shared floor (`WikidataClient`'s `BuildPlayerPoolBirthYearQuery` and friends) from 1939 to 1975 | One change point, no new runtime check in `XGPathGameModule` | Also narrows xG Grid's own player pool — REQ-112 is explicitly shared infrastructure between both games, and xG Grid has no reason to want a 1975 floor | Out of scope per Epic 12's intro paragraph; the two games' eligibility requirements have diverged and a shared upstream floor can no longer serve both without change |
| Fold the check into `PathCareerStintFilter` (stint-level filtering) | Keeps all of xG Path's non-structural filtering logic in one file | `BirthYear` is a `Player`-level fact with no home on a `PlayerCareerStint` row — would require either threading `Player` data into a filter that currently only ever sees stints, or duplicating `BirthYear` onto every stint row, neither of which is a good fit | REQ-1201's own eligibility pipeline (`GetEligiblePlayerIdsAsync`) is the natural place for a player-level check, alongside (not inside) the existing stint-level `IsEligible`/`PathCareerStintFilter` checks — this is the backlog story's own explicit direction |
| Treat `BirthYear == null` as passing (benefit of the doubt) | Larger eligible pool while `BirthYear` backfill coverage is incomplete | Admits a candidate xG Path cannot actually verify meets the new floor — contradicts this codebase's established fail-closed convention (ADR-0070, REQ-211's fallback) | Fail-closed is the deliberate, precedented choice; S-141 is the intended remediation path for shrinking the resulting pool cost over time, not a reason to admit unverifiable data now |

## Consequences

- Positive: xG Path gets a narrower, more relevant target-player floor
  without touching REQ-112's shared upstream infrastructure or affecting
  xG Grid's pool at all.
- Positive: the new check reuses `IPlayerRepository.GetPlayersByIdsAsync`
  (already-existing, bulk-lookup shaped) — no new repository method, no
  new query pattern.
- Negative / trade-offs accepted: every xG Path candidate with a null
  `BirthYear` is excluded from the eligible pool until backfilled, even
  if they would otherwise be a perfectly good target — this shrinks the
  pool by an amount that depends entirely on current `BirthYear` data
  coverage, not on anything about the candidates themselves.
- Follow-up: S-141 (docs/backlog.md) sweeps remaining null-`BirthYear`
  rows to shrink this cost over time. Revisit whether the 1975 threshold
  itself is right once xG Path's actual target pool size and quality can
  be observed post-launch, the same "revisit once observed" precedent
  ADR-0047's own 20-appearance threshold uses.

## For AI agents

Do not raise REQ-112's shared `WikidataClient` birth-year floor to 1975
to implement this — that floor is shared with xG Grid and is explicitly
out of scope here; this decision is xG-Path-only and additive. Do not
fold this check into `PathCareerStintFilter` or `IsEligible` — it is a
`Player`-level fact with no natural home on a `PlayerCareerStint` row, and
belongs in `XGPathGameModule.GetEligiblePlayerIdsAsync` alongside those
checks, not inside them. Do not treat a null `BirthYear` as passing — it
must be excluded, fail closed, matching ADR-0070's precedent; if a task
seems to want the opposite ("null probably just means unknown, let it
through"), stop and re-read this ADR's Decision §2 first. This ADR
supersedes ADR-0045 on this one point only (whether/how a `BirthYear`
floor applies in `XGPathGameModule`'s eligibility pipeline) — ADR-0045
remains the authoritative record for everything else it covers (entity
shape, the FK decision, the "3 stint rows not 3 clubs" reading, the
"undeterminable order" reading); do not relitigate or restate those here.
