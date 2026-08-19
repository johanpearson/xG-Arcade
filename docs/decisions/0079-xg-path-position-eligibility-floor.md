# ADR-0079: xG Path adds a `Player.Position != null` eligibility floor, additive to ADR-0073's `BirthYear` floor

- **Status:** Accepted
- **Date:** 2026-08-19
- **Related requirements:** REQ-1201, REQ-1203, REQ-1207
- **Related components:** COMP-11 (Games.XGPath), COMP-06 (Data.PlayerStore)
- **Related decisions:** ADR-0073 (the `BirthYear` floor this decision
  mirrors almost exactly), ADR-0070 (fail-closed precedent both follow),
  ADR-0074 (`MinDocumentedStintCount`'s "eligibility must guarantee
  REQ-1203's fixed clue sequence" reasoning, referenced in Alternatives
  below)

## Context

A 2026-08-18 user QA report found an xG Path puzzle rendering
"Position: not available" even though the same target's `Nationality`/
`BirthYear` were populated and displayed correctly. `Player.Position`
staying `null` forever for a subset of rows is deliberate, documented
REQ-1207 behavior (a Wikidata data gap, not a bug — see `Player.cs`'s own
comment on the field). The bug is not that `Position` can be `null`; it's
that nothing in `XGPathGameModule.GetEligiblePlayerIdsAsync` stopped a
`Position == null` candidate from being *selected* as a puzzle target in
the first place, unlike `Player.BirthYear`, which ADR-0073/S-137 already
excludes on `null` for the identical reason (fail-closed, additive,
`Player`-level).

This is structurally the same problem ADR-0073 solved for `BirthYear`,
for a second, independent `Player`-level field. The same two questions
from ADR-0073 needed re-deciding for `Position`:

1. **Where does the check live?** Inside `IsEligible`/
   `PathCareerStintFilter` (stint-level), or alongside them in
   `GetEligiblePlayerIdsAsync` (player-level)?
2. **What happens to a candidate with `Position == null` (or empty/
   whitespace)?** Admit it (benefit of the doubt) or exclude it (fail
   closed)?

## Decision

1. **The `Position != null`/non-empty floor lives as an xG-Path-only,
   additive, runtime check in `XGPathGameModule.GetEligiblePlayerIdsAsync`**
   — not inside `IsEligible`/`PathCareerStintFilter`, and not as a new
   repository call. It runs in the exact same pass as ADR-0073's
   `BirthYear` check, reusing the same `playersById` bulk-fetch
   (`IPlayerRepository.GetPlayersByIdsAsync`) already made for `BirthYear`
   — no second `GetPlayersByIdsAsync` call. It is applied alongside the
   `BirthYear` check, not folded into it: the two are independent
   conditions on two independent fields, each individually
   fail-closed.

   It is a `Player`-level check, not a `PlayerCareerStint`-level one, for
   the identical reason `BirthYear` is: `Position` is a fact recorded once
   per player (`Player.Position`), not per career-stint row —
   `PlayerCareerStint` has no `Position` field and no natural way to carry
   one, so there is no clean way to fold this into
   `PathCareerStintFilter`'s stint-level filtering the way the
   national-team/B-team exclusions are folded in there.

2. **`Position == null`, empty, or whitespace-only is excluded, fail
   closed** — matching ADR-0073's and this codebase's established
   convention (ADR-0070, REQ-211's fallback behavior). xG Path cannot
   verify that a candidate with no usable `Position` string has real data
   to render; silently admitting them anyway would be exactly the "can't
   verify it, so treat it as passing" mistake ADR-0070 and ADR-0073 both
   deliberately avoid. The check uses `string.IsNullOrWhiteSpace`, the
   null-tolerant-string equivalent of `BirthYear.HasValue`, rather than a
   bare null check — written this way so the empty/whitespace case is
   visibly a deliberate decision, not an accident of leaving a
   partially-invalid string to pass through.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Wait for a future backfill sweep instead (the S-141-style remediation ADR-0073 already has queued for `BirthYear`) | No code change now; pool shrinks less once backfilled | Does not stop the underlying bug: as long as new rows without `Position` keep entering the pool, or existing null rows are never swept, new preventable "not available" leaks keep surfacing on real puzzle screens indefinitely | A backfill sweep is a legitimate *follow-up* (same as S-141 is for `BirthYear`) but is not a substitute for an eligibility-time guard — it reduces the cost of the guard over time, it doesn't replace the need for one |
| Put the check in `PathClueSequenceBuilder` as a display-time skip (render "Position" turn without a value, or skip that clue) | No change to eligibility pool size | REQ-1203's contract is a fixed 7-turn clue sequence, never a skipped or altered turn — hiding a missing `Position` at display time would violate that contract instead of fixing eligibility, and would still have selected an inherently worse puzzle (missing a clue value) as a target | Rejected for the same category of reason ADR-0074 rejected shrinking `MinDocumentedStintCount`: REQ-1203's fixed-shape guarantee must hold by construction of *who gets selected*, not be patched around at render time |
| Fold `Position` into `PathCareerStintFilter`/`IsEligible` (stint-level) | Keeps all non-structural filtering logic in one file | `Position` is a `Player`-level fact with no home on a `PlayerCareerStint` row — same reasoning ADR-0073 already rejected this option for `BirthYear` | `GetEligiblePlayerIdsAsync` is the established, precedented home for `Player`-level eligibility facts; `IsEligible`/`PathCareerStintFilter` are for stint-level facts only |
| Treat `Position == null` as passing (benefit of the doubt) | Larger eligible pool while `Position` data coverage is incomplete | Admits a candidate xG Path cannot verify has real data to show — contradicts ADR-0070/ADR-0073's established fail-closed convention, and is the exact defect this ADR exists to close | Fail-closed is the deliberate, precedented choice, identical to ADR-0073's `BirthYear` reasoning |

## Consequences

- Positive: closes the exact preventable "Position: not available" defect
  the 2026-08-18 QA report found, without touching display-time code or
  REQ-1203's fixed 7-turn clue-sequence contract.
- Positive: reuses the existing `playersById` bulk-fetch already made for
  the `BirthYear` check — no new repository method, no new query, no
  schema change.
- Negative / trade-offs accepted: the eligible pool shrinks by however
  many currently-eligible candidates lack a `Position` value, on top of
  whatever shrinkage ADR-0073's `BirthYear` floor already causes. This
  cost depends entirely on current `Position` data coverage, not on
  anything about the candidates themselves.
- Follow-up: a future backfill sweep (mirroring S-141's role for
  `BirthYear`) would shrink this cost over time; not solved in this same
  story. No new sweep story is filed as part of this ADR — file one
  separately if/when `Position` null-rate is observed to be a meaningful
  fraction of the eligible pool.

## For AI agents

Do not treat a null, empty, or whitespace-only `Player.Position` as
passing eligibility — it must be excluded, fail closed, matching
ADR-0073's and ADR-0070's precedent; if a task seems to want the opposite
("null probably just means unknown, let it through"), stop and re-read
this ADR's Decision §2 first. Do not fold this check into
`PathCareerStintFilter` or `IsEligible` — it is a `Player`-level fact with
no natural home on a `PlayerCareerStint` row, and belongs in
`XGPathGameModule.GetEligiblePlayerIdsAsync` alongside the `BirthYear`
check, not inside it, and not merged into the same `Where` condition as
`BirthYear` in a way that would make the two checks harder to reason
about independently. Do not add a second `IPlayerRepository.
GetPlayersByIdsAsync` call for this — reuse the `playersById` bulk-fetch
already made for the `BirthYear` check; they run against the exact same
`structurallyEligibleIds` set. Do not attempt to fix this by skipping or
altering a turn in `PathClueSequenceBuilder`/`PathEndpoints.cs` display
code instead — REQ-1203's fixed 7-turn sequence must never be
short-circuited at display time; the fix belongs at eligibility-selection
time only, per this ADR.

This change was authored and hand-traced in a sandbox with no `dotnet`
SDK, no database access, and no wikidata.org access (same constraints
ADR-0075's and ADR-0078's own "For AI agents" sections disclose) — the
production code and its `NUnit` tests were written by mirroring
ADR-0073's already-shipped `BirthYear` pattern field-for-field, but
neither has been compiled nor run. Before trusting this in production,
run `dotnet test` for real against
`XGArcade.Games.XGPath.Tests/XGPathGameModuleTests.cs` in an environment
where the SDK is available.
