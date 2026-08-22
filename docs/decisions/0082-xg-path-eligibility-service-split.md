# ADR-0082: Split `PathEligibilityService` out of `XGPathGameModule`

- **Status:** Accepted
- **Date:** 2026-08-22 (S-154, `docs/backlog.md` Epic 17)
- **Related requirements:** REQ-1201 (target-player eligibility), REQ-1203
  (fixed 3-turn club-reveal clue structure — the invariant this split
  preserves byte-for-byte)
- **Related components:** COMP-11 (Games.XGPath)

## Context

`XGPathGameModule.cs` had grown to 557 lines. `CODE_HEALTH_ASSESSMENT.md`'s
2026-08-11 revision flagged it pre-emptively as "already shows the same
multi-concern-method pattern XGGrid took at this size — a clear pre-emptive
refactor candidate," referring to the same SRP outlier `GridGameModule.cs`
was before ADR-0068. That prediction materialized: by S-154, the file was
557 lines with 8 commits since S-081 (S-137/138/139/141), the highest churn
count of any non-generated backend file besides `CliVerbDispatcher.cs`
(whose growth is healthy verb-registry breadth, not concern-mixing).

`GetEligiblePlayerIdsAsync`/`IsEligible` (~150 lines together) formed a
genuinely separable "eligibility pipeline" concern — candidate narrowing via
a cheap projection, full stint loading, national-team/B-team sanitization,
adjacent-same-club collapsing, three structural checks (documented-stint
floor, chronological-order determinability, distinct-qualifying-seeded-club
count), the BirthYear/Position floors, and ADR-0056's familiarity filter —
distinct from `GenerateInstanceAsync`'s own orchestration concern (template
lookup, cycle rollover, target selection, persistence) and from
`ScoreSubmissionAsync`'s scoring concern. Every one of ADR-0073, ADR-0074,
and ADR-0079's stories touched this same method for an eligibility-rule
reason, never for a generation- or scoring-orchestration reason — the same
signal ADR-0068 used to identify `GridGameModule`'s three concerns.

`docs/decisions/0068-grid-game-module-responsibility-split.md` had already
established the precedent for this exact shape of problem in the sibling
game module, `GridGameModule.cs`: extract the separable concern(s) into
their own narrowly-dependent class(es), keep `IGameModule` implemented
directly on the original type (a real external contract, not something to
split), and add no facade. S-154's own backlog text explicitly calls for
mirroring that precedent "exactly."

## Decision

Extract `GetEligiblePlayerIdsAsync`/`IsEligible` into a new
`IPathEligibilityService`/`PathEligibilityService`, a narrow single-method
interface (`GetEligiblePlayerIdsAsync`) matching `IGridGenerationService`'s
"narrow interface" shape. Registered independently in
`ServiceRegistration.cs` (`AddScoped`, same lifetime as the original
registration, immediately before `AddScoped<IGameModule, XGPathGameModule>()`).

**No facade.** `XGPathGameModule` keeps implementing `IGameModule` directly
— unlike ADR-0067's repository split, `IGameModule` is a real,
externally-depended-on contract (`Core.Scoring`, `Core.Rounds`,
`XGArcade.Api`, `IGameModuleResolver` all resolve xG Path through it,
ADR-0003) that must see zero change. `XGPathGameModule` now injects
`IPathEligibilityService` narrowly, alongside its other unchanged
dependencies (`IPathInstanceRepository`, `IPlayerRepository`,
`IPlayerAliasRepository`, `IPlayerCareerStintRefreshService`, `Random?`,
`TimeProvider?`).

`PathEligibilityService` depends on `IPlayerCareerStintRepository`,
`IPlayerRepository`, `ICategoryValueRepository`, `IPlayerFamiliarityService`
— exactly the four the eligibility pipeline itself needs. `IPlayerRepository`
is now injected on both classes: a small, deliberate, accepted duplication
(`XGPathGameModule.ScoreSubmissionAsync` still needs it directly for
name-based correctness resolution), the same shape ADR-0068 accepted for
`IGridInstanceRepository` being injected on both `GridGenerationService` and
`GridGameModule` rather than routing every access through one service for
no reason but avoiding a second injection.

The REQ-1203 fetch→sanitize→collapse→eligible-check ordering invariant
comment (locked by S-139, extended by ADR-0081) moved with the code it
documents, byte-for-byte verbatim — verified by diff against the
pre-refactor file, not just behavioral equivalence, since that invariant is
exactly the kind of thing a paraphrase-during-move could silently drift.
The four eligibility constants (`MinAppearancesAtSeededClub`,
`MinQualifyingSeededClubs`, `MinDocumentedStintCount`, `MinBirthYear`) moved
with the code that uses them; `MaxAttemptsPerPuzzle` (REQ-1205, unrelated to
eligibility) stayed on `XGPathGameModule`.

`XGPathGameModuleTests.cs` (1493 lines, 50 test methods) split 1:1, matching
ADR-0068's own "mechanical method-name diff, zero drops, zero duplicates"
bar for `GridGameModuleTests.cs`'s split: 26 eligibility-rule tests
moved/renamed (`REQ####_GenerateInstanceAsync_...` →
`REQ####_GetEligiblePlayerIdsAsync_...`, since the method under test now
lives on `PathEligibilityService`) into a new `PathEligibilityServiceTests.cs`,
reshaped to assert directly on `GetEligiblePlayerIdsAsync`'s returned id
list (`Does.Contain`/`Does.Not.Contain`) rather than the original's indirect
"insufficient pool → `PathGenerationException`" proxy technique — the same
"reshaped to assert directly on the narrower unit" allowance ADR-0068's own
Decision section used for its REQ-211 tests. The remaining 24
adapter-orchestration tests (generation/cycle/scoring/attempt-cap/REQ-215/
REQ-216) stayed in `XGPathGameModuleTests.cs`, whose `BuildModule` now
composes a real `PathEligibilityService` rather than a fake, matching
`GridGameModuleTests`'s own post-ADR-0068 "compose the real thing"
precedent. `XGPathGameModule.cs` itself went from 632 to 291 lines.

Reviewed by `architecture-reviewer` and `quality-architect` (commit
`490186a`) before this ADR was written — both confirmed the extraction is
architecturally sound (no facade, `IGameModule` contract unchanged, module
boundaries respected — `PathEligibilityService` never touches
`XGArcadeDbContext` directly or `PlayerAttribute`/`PlayerOverride`) and that
the test split is a clean structural move with zero dropped or duplicated
coverage.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Leave `XGPathGameModule` as one wide class | Zero migration risk; no call-site/DI churn | The SRP violation persists indefinitely — confirmed by 8 commits' worth of churn concentrated on the same method for unrelated eligibility-rule changes | Rejected — same reasoning ADR-0068 applied to the equally-shaped `GridGameModule` outlier |
| Split further — separate the BirthYear/Position Player-level floors from the structural stint checks into their own class(es) | Even smaller individual files | These are one cohesive "eligibility" concern, read and changed together in practice — every ADR-0073/ADR-0074/ADR-0079 story touched the same method for the same reason, regardless of which specific check it added; finer splitting would fragment a concern nobody actually changes independently | Rejected — matches the story's explicit single-service scope; over-splitting trades one god-class smell for a different one (too many tiny collaborators), the same reasoning ADR-0068 gave for not splitting `GridGameModule` further than its three natural concerns |
| Delete `XGPathGameModule.cs` entirely, have callers depend on `IPathEligibilityService` directly | Removes one layer of indirection | `IGameModule` is a real, externally-depended-on contract (`Core.Scoring`, `Core.Rounds`, `XGArcade.Api`, `IGameModuleResolver` all resolve xG Path through it, ADR-0003) that must stay generic — there is no equivalent "delete the wide interface" step available | Rejected — not applicable; `XGPathGameModule` must keep implementing `IGameModule` regardless of internal structure, the identical reasoning ADR-0068 gave for `GridGameModule` |

## Consequences

- **Positive:** `PathEligibilityService`'s constructor now documents, at a
  glance, exactly which four dependencies the eligibility concern needs,
  instead of that being buried among `XGPathGameModule`'s other six
  constructor dependencies; `XGPathGameModule.cs` is now legible as a pure
  `IGameModule` adapter (291 lines, down from 632) rather than a place new
  eligibility logic accretes — the same clarity gain ADR-0068 reports for
  `GridGameModule.cs` shrinking to ~160 lines.
- **Negative / trade-offs accepted:** `IPlayerRepository` is now injected on
  both `XGPathGameModule` and `PathEligibilityService` — a small, deliberate
  duplication of which concerns touch the `Player` table, in exchange for
  each class's constructor accurately reflecting what it actually needs,
  the same trade-off ADR-0068 accepted for `IGridInstanceRepository`.
- **Follow-up:** none identified — this closes S-154 with no deferred work.

## For AI agents

Do not merge `PathEligibilityService` back into `XGPathGameModule`, and do
not add a facade/umbrella interface composing them — a caller needing both
concerns injects both narrowly, per this ADR's own "no facade" decision
(same precedent as ADR-0068). `XGPathGameModule` must keep implementing
`IGameModule` directly and must not grow new eligibility-rule logic of its
own — a new REQ-1201 structural check, floor, or filter belongs on
`PathEligibilityService`; new generation-orchestration or scoring logic
belongs on `XGPathGameModule` itself. Do not reorder or paraphrase the
REQ-1203 fetch→sanitize→collapse→eligible-check invariant comment on
`PathEligibilityService.GetEligiblePlayerIdsAsync` — see that comment and
ADR-0081 for why the ordering is load-bearing, not stylistic.
