# ADR-0111: xG Higher/Lower's stat categories are derived as counts of existing PlayerAttribute rows, not new numeric fields

- **Status:** Accepted
- **Date:** 2026-09-09
- **Related requirements:** REQ-1501, REQ-1502, REQ-203, REQ-501
- **Related components:** COMP-06 (Data.PlayerStore), COMP-18 (Games.XGHigherLower)

## Context

S-224 needed to implement `XGHigherLowerGameModule.GenerateInstanceAsync` against
REQ-1501, which requires every stat category used in xG Higher/Lower to be
"numeric and directly comparable between any two players, sourced from
`PlayerAttribute`/`PlayerOverride` (COMP-06) — never from a new external
data source." REQ-1501's own illustrative example list ("international
caps, career goals, market value, league titles won") reads as a set of
real per-player numeric statistics.

`PlayerAttribute` (`backend/src/XGArcade.Data/Entities/PlayerAttribute.cs`)
does not store anything like that today. Its `AttributeType` is one of
exactly three categorical strings — `"club"`, `"nationality"`, `"trophy"`
— and its shape is "one row per distinct value" (one row per career club,
one row per trophy won), populated by `WikidataLookupService`/
`PlayerCareerPrefetchService` for xG Grid's candidate-matching use case
(REQ-101). No numeric field (goals, caps, a market-value figure) exists
anywhere in `PlayerAttribute`, `PlayerOverride`, or the `DataSync` client
layer. Building one would mean a new external data source and a new sync
pipeline — exactly what REQ-1501 explicitly rules out, and squarely the
kind of Tier 1/2 pull-forward `MVP-SCOPE.md`/`CLAUDE.md` require flagging
rather than quietly building.

So the real question S-224 had to resolve, that neither REQ-1501 nor
ADR-0110 answers directly: given the data that actually exists, what does
"a numeric stat category sourced from `PlayerAttribute`/`PlayerOverride`"
concretely mean?

## Decision

**A candidate stat category's numeric value is the COUNT of a player's
effective `PlayerAttribute` rows for one `AttributeType`.** Two
`AttributeType`s are used as candidates: `"trophy"` (trophy count — a
direct, if broader, realization of REQ-1501's own "league titles won"
example) and `"club"` (career-clubs-represented count). `"nationality"`
is permanently excluded: a player has virtually always exactly one
nationality value, so any two players would almost always tie, defeating
REQ-1502's entire no-exact-tie purpose.

**Override precedence (ADR-0015) extends from a single-value check to a
count, using the same rule, not a new one:** ADR-0015 established that a
`PlayerOverride` for `(PlayerId, Field)` replaces the *entire* effective
value set for that attribute type, never merges with cached rows. Extended
to counting, a `PlayerOverride` for `(PlayerId, AttributeType)` makes that
player's effective count exactly 1 (the override's own single `Value`),
regardless of how many raw `PlayerAttribute` rows of that type exist —
even zero. A player with neither an override nor any raw rows of that
type has no value for the category at all (REQ-1501's "non-null value"
eligibility rule) — this is a third state, not a count of 0, preserved by
returning such players absent from `GetEffectivePlayerCountsByAttributeTypeAsync`'s
result dictionary rather than present with value `0` (`PlayerOverrideRepository.cs`).

No new entity, no new external data source, no new `DataSync` ingestion —
100% sourced from data the existing Wikidata sync already populates today.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Count of effective `PlayerAttribute` rows per type (chosen)** | Zero new data sourcing, ships as part of S-224 with no dependency on new ingestion work; directly realizes REQ-1501's own "league titles won" example via trophy count | "Club count" reads as a "well-traveled" stat, not an "accomplishment" stat the way every other REQ-1501 example does — a real, if minor, mismatch in gameplay feel (see Follow-up) | Chosen: satisfies REQ-1501's literal text (numeric, comparable, sourced from COMP-06, no new external source) using only data that already exists, keeping this Tier 0 |
| Build real numeric player stats (career goals, caps, market value) via a new DataSync source | Matches REQ-1501's illustrative examples exactly; more "obviously correct" gameplay feel | New external data source + new sync pipeline + terms-of-service review (per CLAUDE.md's "new external data sources need a terms-of-service check first") — genuine Tier 1/2 scope, explicitly what REQ-1501 rules out and what `MVP-SCOPE.md` says not to pull forward without being an explicit backlog decision | Rejected: out of S-224's scope, and out of Tier 0 entirely unless deliberately pulled forward as its own story |
| Block S-224 and send REQ-1501 back to `requirements-writer` for clarification before writing any generation code | Removes any risk of a unilateral interpretation being wrong | Blocks the whole story on a question whose answer ("use what already exists, don't build new ingestion") is already implied by REQ-1501's own explicit "never a new external data source" clause and by `MVP-SCOPE.md`'s build-order rule | Rejected: the requirement's own text already rules out the alternative (new data sourcing), leaving "derive from existing data" as the only Tier-0-compatible reading — not a genuinely open question requiring a stop |

## Consequences

- Positive: S-224 ships with zero new external dependencies or ingestion
  work, fully within Tier 0.
- Positive: "trophy count" is a clean, defensible realization of REQ-1501's
  own named example, with no interpretive stretch.
- Positive: override precedence is a direct, mechanical extension of
  ADR-0015's already-established rule (`PlayerOverrideRepository.
  GetEffectivePlayerCountsByAttributeTypeAsync`) — no new precedence rule
  invented, and it is unit-tested against the same absent-vs-zero
  distinction `IPlayerAttributeRepository`'s bulk-read methods already use.
- Negative / trade-off accepted: "club count" is a materially different
  *kind* of stat than REQ-1501's other examples — "more clubs" does not
  read as "more accomplished" the way "more trophies," "more goals," or
  "more caps" do. This was flagged during this story's own review
  (`architecture-reviewer` pass) as a gameplay-design judgment call, not a
  structural one.
- Negative / trade-off accepted: an admin adding a `PlayerOverride` to
  correct one specific `club`/`trophy` value for a player who has several
  now also silently collapses that player's Higher/Lower effective count
  for the corresponding category to exactly 1 — a side effect of REQ-501's
  admin-correction feature that neither REQ-501 nor ADR-0015 was written
  with this derived-numeric-stat use case in mind for. Flagged during
  review (`quality-architect` pass) for `requirements-writer` awareness,
  not treated as a defect to fix here — the alternative (a different
  override-precedence rule just for count derivation) would itself be a
  new, undocumented precedence rule, worse than the one accepted here.
- Follow-up: **get explicit product confirmation on "club count" specifically**
  before it is ever played by a real user — S-224 only implements
  generation; no Round is actually schedulable yet (S-226/S-227 wire this
  `GameKey` into `RoundSchedulingOptions`/`InternalRoundEndpoints`), so
  there is time to revisit before this is live. If rejected, removing
  `"club"` from `XGHigherLowerGameModule.CandidateStatCategories` is a
  one-line change with no schema impact — `HigherLowerInstance.StatCategory`
  is a plain string, not an enum requiring migration.
  If accepted, keep as-is with an added test to `PlayerOverrideRepositoryTests`/
  `XGHigherLowerGameModuleTests` if any new nuance emerges.
- Follow-up: if a real numeric stat (career goals, caps, market value)
  is ever sourced via a new `DataSync` provider later (its own story, its
  own terms-of-service check per CLAUDE.md), `CandidateStatCategories`
  can grow to include it without changing `HigherLowerInstance`'s schema —
  `StatCategory` is already a free-form string, not constrained to
  `PlayerAttribute.AttributeType`'s current three values.
- Follow-up: `requirements-writer` should confirm whether ADR-0015's
  "override replaces the whole type" language should explicitly extend to
  a derived-count use case, or whether a future override-CRUD story
  (mirroring S-012's own follow-up note in ADR-0015) needs to warn an
  admin that a `club`/`trophy` override also affects xG Higher/Lower
  eligibility, not just xG Grid correctness-checking.

## For AI agents

Do not add a new numeric `PlayerAttribute`/`Player` field, and do not add
a new `DataSync` provider, to give xG Higher/Lower a "more realistic"
numeric stat — that is out of Tier 0 scope unless pulled forward as its
own deliberate backlog story with its own terms-of-service check. Stat
categories for this game are, until such a story exists, exactly the
count-of-effective-`PlayerAttribute`-rows-per-type computed by
`IPlayerOverrideRepository.GetEffectivePlayerCountsByAttributeTypeAsync`,
restricted to `XGHigherLowerGameModule.CandidateStatCategories`. If code
you are about to write would contradict this decision, stop and flag it
rather than silently working around it — either this ADR needs a
superseding one, or the approach needs to change.
