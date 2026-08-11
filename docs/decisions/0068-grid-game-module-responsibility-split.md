# ADR-0068: Split `GridGameModule` by responsibility (generation / name-matching / live-lookup dispatch)

- **Status:** Accepted
- **Date:** 2026-08-11 (S-119, `docs/backlog.md` Epic 9)
- **Related requirements:** none (pure refactor, no behavior change — same
  "structural-only" status ADR-0067 carried for the equivalent
  `PlayerStoreRepository` split)
- **Related components:** COMP-05 (Games.XGGrid)

## Context

`GridGameModule.cs` had grown to 1,039 lines, 26 methods, and 13
constructor-injected dependencies — confirmed the clearest Single
Responsibility Principle outlier in `XGArcade.Games.XGGrid`
(`CODE_HEALTH_ASSESSMENT.md` scored it 4.5/10, one of only three hotspots
below 5.0 platform-wide, alongside `WikidataClient.cs` and the frontend's
missing data-fetching abstraction). Epic 7's S-104 had already flattened
its nesting (25→3 deep-indent lines), but that story was explicitly scoped
to nesting depth, not responsibility count — S-104 left the method/
dependency counts untouched.

The single class covered at least four genuinely distinct concerns: grid
generation (pairing selection, header picking against `MinValidAnswers`/
`MaxDuration`, cell construction — REQ-101/102/107/108), three-stage name
matching and disambiguation (exact → alias → fuzzy, REQ-207/208/209),
live-lookup dispatch (routing a Country/Club/Trophy pairing to the right
`IWikidataLookupService` method, both at generation time and as REQ-211's
guess-time fallback), and the `IGameModule` contract itself (the public
surface every caller outside `Games.XGGrid` — `GuessSubmissionService`,
`LeaderboardEndpoints`, `RoundEndpoints`, `IGameModuleResolver`, and others
— depends on). Every caller needing any one of these concerns depended on
the same 13-dependency class regardless of which one it actually used,
exactly the same failure mode ADR-0067 diagnosed for the pre-split
`IPlayerStoreRepository` — just on the responsibility axis (SRP) rather
than the entity axis `IPlayerStoreRepository` was split along.

## Decision

Split the four concerns into three new classes, each behind its own narrow
interface, plus the existing `IGameModule` interface which `GridGameModule`
keeps implementing directly (unlike ADR-0067's repository split, the public
contract here — `IGameModule` — has real external callers across
`Core.Scoring`/`Core.Rounds`/`XGArcade.Api` that must see zero change, so
there is no equivalent of "delete the original file" — `GridGameModule.cs`
stays, shrunk to a thin composing adapter):

- **`IGridGenerationService`/`GridGenerationService`** — `GenerateInstanceAsync`'s
  full pipeline: pairing selection, row/column header picking against
  `GridGenerationOptions`' thresholds, match-count caching/live-lookup
  fallback, cell construction. Depends on `IGridInstanceRepository`,
  `ICategoryValueRepository`, `IPlayerAttributeRepository`,
  `IGridLiveLookupDispatcher` (below), `GridGenerationOptions`.
- **`IGridNameMatcher`/`GridNameMatcher`** — the REQ-207/208/209 three-stage
  matching pipeline (exact → alias → fuzzy), disambiguation-candidate
  construction, and REQ-216's wrong-guess name/photo resolution. Depends on
  `IPlayerRepository`, `IPlayerAliasRepository`, `IPlayerAttributeRepository`,
  `IPlayerOverrideRepository`, `IPlayerNameIndexRepository`, an optional
  `IWikidataClient?`.
- **`IGridLiveLookupDispatcher`/`GridLiveLookupDispatcher`** — the single
  place that decides which `IWikidataLookupService` method a given
  Country/Club/Trophy pairing routes to, shared by both callers that need
  it: `GridGenerationService.GetMatchCountAsync` (generation-time cache-miss
  fallback) and this class's own `TryRefreshCellAsync` (REQ-211's
  guess-time fallback, including the ADR-0052 persistent-failure
  short-circuit and the ADR-0046 `WikidataQueryException` →
  `LiveLookupUnavailableException` translation at the DataSync/Games.XGGrid
  boundary). Depends on `ICategoryValueRepository`, `IWikidataLookupService`,
  `IPlayerDataQualityRepository`.

A `CategoryCandidate` record struct (row/column header candidate,
abstracted away from which reference table it came from) moved from a
private nested type on the old god-class to its own file, `internal` at
namespace scope — shared by `GridGenerationService` and
`GridLiveLookupDispatcher`, so it can no longer be private to one class.

`CategoryPairingRules` gained one new public static method,
`MapAttributeType` (the `"country"→"nationality"`/`"club"→"club"`/
`"trophy"→"trophy"` mapping between `GridCell`'s category-type vocabulary
and `PlayerAttribute.AttributeType`'s vocabulary), moved from a private
method on the old god-class. All three new classes need this exact mapping
identically — unlike ADR-0067's `GroupByPlayerIdAsync` helper (deliberately
*duplicated* per repository, since each copy varied by which entity it
grouped), `MapAttributeType` is a single, stateless, dependency-free lookup
table with exactly one correct implementation, so tripling it across three
files would only risk silent drift between copies with no offsetting
benefit. `CategoryPairingRules`'s own pre-existing doc comment already
referenced this cross-vocabulary relationship before this change ("Distinct
from `PlayerAttribute.AttributeType`'s vocabulary...") — this is a natural
extension of a responsibility the class already partially carried, not new
scope grafted on.

Every new interface is registered independently in
`CompositionRoot/ServiceRegistration.cs` (`AddScoped`, same lifetime as the
original registration). **No facade was added** — `GridGenerationService`
injects `IGridLiveLookupDispatcher` directly (the one cross-dependency
between the new classes, matching the shared-dispatch reasoning above);
`GridGameModule` injects all three narrowly, plus `IGridInstanceRepository`
and `IPlayerNameIndexRepository` directly for the small amount of
orchestration logic that has no other owner (the REQ-211
`ExistsByNormalizedNameAsync` gate check, and the three trivial
single-repository-call `IGameModule` methods — `GetCellIdsAsync`,
`GetCellCategoryTypesAsync`, `GetMaxAttemptsForCellAsync` — that don't
belong to any of generation/matching/live-lookup).

Existing `GridGameModuleTests.cs` coverage (2,345 lines, 90 test methods)
moved/renamed into `GridGenerationServiceTests.cs`/`GridNameMatcherTests.cs`/
`GridLiveLookupDispatcherTests.cs`, plus a slimmed `GridGameModuleTests.cs`
retaining only the adapter's own orchestration tests — test bodies/
assertions are unchanged where they moved, this is a structural move only,
verified by a mechanical method-name diff against the original file
(confirmed 1:1, zero drops, zero duplicates). A handful of REQ-211 tests
were reshaped from asserting on `ScoreSubmissionAsync`'s full `ScoreResult`
to asserting directly on `TryRefreshCellAsync`'s own boolean/exception
contract, since that method's `true` return means "the pairing was
resolvable," not "a match was found" — a legitimate narrowing to the unit
actually under test, not a weakening.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Leave `GridGameModule` as one wide class | Zero migration risk; no call-site/DI churn | The 26-method/13-dependency SRP violation persists indefinitely (confirmed the platform's second-worst code-health hotspot); every future change to matching, generation, or live-lookup dispatch keeps touching the same 1,039-line file | Rejected — same reasoning ADR-0067 applied to the equally-sized `IPlayerStoreRepository` outlier |
| Split further — one class per `IGameModule` method, or extract `BuildDisambiguationCandidatesAsync`/`GetDistinguishingAttributeValues` etc. into their own classes too | Even smaller individual files | The three concerns already map cleanly to how the code is actually read/changed (nobody changes fuzzy-match tolerance and grid-pairing feasibility in the same commit); finer splitting would fragment cohesive logic (e.g. the three-stage matching pipeline) across files for no real independent-reuse benefit — the backlog story's own scope (`docs/backlog.md` S-119) named exactly these three classes | Rejected — matches the story's explicit scope; over-splitting trades one god-class smell for a different one (too many tiny collaborators) |
| Delete `GridGameModule.cs` entirely and have callers depend on the three new interfaces directly, mirroring ADR-0067's "no facade, not even the original wide type" outcome | Removes one extra layer of indirection | `IGameModule` is a real, externally-depended-on contract (`Core.Scoring`, `Core.Rounds`, `XGArcade.Api`, `IGameModuleResolver` all resolve xG Grid through it) that ADR-0003 requires stay generic — there is no equivalent "delete the wide interface" step available here, since `IGameModule` was never the thing being split | Rejected — not applicable; `GridGameModule` must keep implementing `IGameModule` regardless of internal structure |

## Consequences

- **Positive:** each new class's constructor signature now documents, at a
  glance, which one of generation/matching/live-lookup-dispatch it actually
  touches, instead of blanket depending on all 13 of the original's
  dependencies; `GridGameModule.cs` itself is now ~160 lines — legible as a
  pure `IGameModule` adapter, not a place new logic accretes; the shared
  live-lookup dispatch table (`LookupMatchesAsync`) is now visibly the one
  piece of logic both generation-time and guess-time code paths depend on,
  rather than being buried as a private method inside a class that looked
  like it only handled guess scoring.
- **Negative / trade-offs accepted:** `GridGameModule`'s own constructor
  still takes 5 dependencies (`IGridInstanceRepository`,
  `IPlayerNameIndexRepository`, plus the three new services) rather than 1
  — a deliberate, smaller version of the same trade-off ADR-0067 accepted,
  in exchange for each dependency's type now saying exactly what it's for;
  `GridGenerationService`/`GridGameModule` both now depend on
  `IGridInstanceRepository` (a small, accepted duplication of which
  concerns touch the grid-instance table, rather than routing every
  instance/cell lookup through one of the other services for no other
  reason than avoiding two injections of the same repository).
- **Follow-up:** two stale doc-comment references caught by
  `quality-architect` review (a `MapAttributeType` comment pointing at
  `GetMatchCountAsync` "below" when that method moved to
  `GridGenerationService.cs`, and two `LookupLiveMatchesAsync` references
  in `CategoryCandidate.cs`'s doc comment naming the pre-refactor method
  name) were fixed in the same PR, not deferred.

## For AI agents

Do not merge `IGridGenerationService`/`IGridNameMatcher`/
`IGridLiveLookupDispatcher` back into `GridGameModule`, and do not add a
facade/umbrella interface composing them — a caller needing more than one
narrowly injects more than one, per this ADR's own "no facade" decision
(same precedent as ADR-0067). `GridGameModule` must keep implementing
`IGameModule` directly and must not grow new business logic of its own —
new generation logic belongs on `GridGenerationService`, new matching logic
on `GridNameMatcher`, new live-lookup-pairing logic on
`GridLiveLookupDispatcher`; `GridGameModule` itself should only ever
orchestrate calls between them plus the small set of trivial
single-repository-call `IGameModule` methods it already owns directly
(`GetCellIdsAsync`, `GetCellCategoryTypesAsync`,
`GetMaxAttemptsForCellAsync`). When adding a new category-type-to-
attribute-type mapping need, extend `CategoryPairingRules.MapAttributeType`
— do not reintroduce a private per-class copy.
