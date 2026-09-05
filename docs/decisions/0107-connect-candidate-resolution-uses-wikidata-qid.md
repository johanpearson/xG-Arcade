# ADR-0107: xG Connect candidate/target-pick resolution uses WikidataQid, not name alone

- **Status:** Accepted
- **Date:** 2026-09-05
- **Related requirements:** REQ-207, REQ-1404, REQ-1406
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync), COMP-10 (Data.PlayerNameIndex), COMP-17 (Games.XGConnect)

## Context

`ConnectChainStepService.SubmitChainStepAsync` and `ConnectTargetPickService
.SubmitTargetPickAsync` have always resolved a client-supplied player NAME
to a real `Player.Id` via `IPlayerRepository
.GetPlayersByNormalizedFullNameAsync` (COMP-06), then — on a same-name
collision (more than one `Player` row with that normalized name) —
deterministically picked whichever sorts lowest by `Id`. Both services'
own doc comments already named this explicitly as "a known, deliberate
simplification, not a new REQ": no client-supplied disambiguation id
existed for either endpoint, since the only client-side search UI
(`/players/autocomplete`, COMP-10) returns `PlayerNameIndex.PlayerId` — a
synthetic, QID-derived hash living in a completely different, unreconciled
id space from `Player.Id` (see `PlayerNameIndex.PlayerId`'s own doc
comment, and ADR-0007's original "separate data source" decision).

A real, reported incident confirmed this "simplification" is a genuine
bug, not a theoretical edge case. Two different real footballers are both
named "Jonas Olsson": one born 1983 (West Bromwich Albion, then a short
2019 loan at Wigan Athletic — the player a user meant), and one born 1994
(a lower-league goalkeeper, Brommapojkarna/Degerfors/GIF Sundsvall, with no
connection whatsoever to West Brom or Wigan). Both are Swedish, so both
plausibly got indexed as separate, individually-correct `Player` rows via
this codebase's own routine per-nationality Wikidata sweeps
(`PlayerCareerPrefetchService`/`PlayerNameIndexImporter`). The user
reported the SAME target player ("Jonas Olsson") failing to connect via
TWO different, independently real, correct connections (Reece James via
Wigan Athletic, and separately Markus Rosenberg via West Bromwich Albion)
— the common factor across both failures being the one player whose
identity resolution had no way to pick the right one of two real people
sharing a name.

ADR-0106 (shipped the same week) had already fixed a real Wikidata-parsing
gap that was ALSO contributing to failed connections — but reproducing the
Reece James/Wigan Athletic case after that fix confirmed a second, deeper
issue: `ConnectChainStepService`'s candidate almost certainly resolved to
the WRONG "Jonas Olsson" (the 1994-born goalkeeper, who genuinely has zero
career overlap with Reece James or Markus Rosenberg), not the correct one
— a distinct bug from anything ADR-0106 addressed.

## Decision

`/players/autocomplete`'s response (`PlayerAutocompleteSuggestion`) now
carries the suggestion's `WikidataQid` (nullable — see Consequences).
`PlayerNameIndex` gains a matching `WikidataQid` column, populated by
`PlayerNameIndexImporter` (already computing it as this method's own
input, just never persisting it before now) and backfilled on any future
re-import via `PlayerNameIndexRepository.UpsertManyAsync`'s existing
update-in-place branch.

`ChainBuilder.tsx` and `TargetPickPanel.tsx` now require a real
`/players/autocomplete` suggestion to be clicked before their submit
button enables (`TargetPickPanel.tsx` already required this;
`ChainBuilder.tsx`'s candidate field previously allowed submitting typed
text with no selection at all). Whichever suggestion is clicked has its
`wikidataQid` carried through to submission alongside its name.

Server-side, a new shared `ConnectCandidateResolver`
(`Games.XGConnect`) replaces both services' own separate name-resolution
logic: when a `WikidataQid` is supplied and is a syntactically valid QID,
it resolves the exact real person via `IPlayerRepository
.GetOrCreatePlayersByWikidataQidAsync` (get-or-create, so a player
who's been indexed but never before referenced by any game module still
resolves cleanly rather than 404ing) — never the ambiguous name-only path.
When no QID is supplied (or it fails validation), resolution falls back to
the exact same name-only, lowest-Id-on-collision behavior as before this
ADR.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Thread WikidataQid through autocomplete → submission (chosen) | Closes the bug class using an identifier this codebase already has and already trusts (Player.WikidataQid, COMP-06's existing unique-per-real-person anchor); no new UI concept — clicking a suggestion is already required for TargetPickPanel; get-or-create means an as-yet-unreferenced player still works | Requires a schema addition (PlayerNameIndex.WikidataQid) and a transition window where older-indexed rows have no QID until the next `import-player-name-index` run | Directly closes a live, twice-reported correctness bug with a mechanism this codebase already has infrastructure for (GetOrCreatePlayersByWikidataQidAsync already existed, built for the recent-transfer-arrival use case) |
| Show a disambiguation prompt only when a same-name collision is detected (never change the default flow) | Smaller change; only the rare collision case sees new UI | Still requires knowing you're in a collision at submission time (the same identity problem, just deferred one step); a genuinely new UI pattern for exactly one game module, when a suggestion-click UI already exists and already needs no new interaction pattern | More code for a narrower fix that doesn't reuse anything already built |
| Add birth year/nationality as an extra required field the player types to disambiguate | No schema change | Puts the disambiguation burden on the player for information autocomplete already has and already displays (BirthYear); doesn't scale past two colliding people; REQ-1406's whole design intent is "the player already knows who they mean," not "make them prove it with metadata" | Worse UX for no structural benefit over reusing the suggestion the player already clicked |

## Consequences

- Positive: closes a real, twice-reported correctness bug (two different
  real "Jonas Olsson"s) at its root — the identity-resolution layer both
  xG Connect screens share — rather than working around it per incident.
- Positive: `ConnectCandidateResolver` is now the ONE place both services'
  candidate-resolution logic lives, closing the "two near-identical copies
  that can silently drift apart" risk both services' own prior comments
  already flagged as a "known, deliberate simplification."
- Negative / trade-off accepted (transition window): a `PlayerNameIndex`
  row indexed before this column existed has `WikidataQid = null` until
  the next `import-player-name-index` run. Every suggestion for such a
  player still works today — via the exact same name-only fallback this
  codebase already relied on before this ADR — but doesn't yet get the
  disambiguation benefit until reimported. `import-player-name-index.yml`
  is `workflow_dispatch`-only (ADR-0007's own follow-up note); triggering
  it is an operational step, not a code change, after this ships.
- Negative / trade-off accepted: `ChainBuilder.tsx`'s candidate field now
  requires a suggestion click, where it previously allowed submitting
  typed-and-unselected text directly. This is a real, deliberate UX
  tightening (matching `TargetPickPanel.tsx`'s existing requirement) — a
  player who knows an exact name but doesn't click the matching suggestion
  can no longer submit it. Judged the correct trade-off: the alternative
  is reintroducing the exact same-name-collision bug this ADR exists to
  close.
- Follow-up: this ADR does not remove the name-only fallback path — it
  stays as a transition/backward-compatibility mechanism (see the
  "Negative" bullet above). If a future review finds it's never actually
  exercised once `import-player-name-index` has fully backfilled
  production, removing it becomes a candidate simplification — not a
  correctness requirement, since it degrades safely today.

## For AI agents

Do not reintroduce a bare-name-only candidate/target-pick resolution path
in `Games.XGConnect` without re-reading this ADR first — the "deterministic
lowest-Id on a same-name collision" shortcut it replaces was already
flagged in code as a known simplification once, and a real incident then
confirmed it as a genuine, twice-reported bug. Any new xG Connect
player-identity-resolution code should go through
`ConnectCandidateResolver`, not a third, independent copy of this logic.
