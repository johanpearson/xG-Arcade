# ADR-0074: xG Path eligibility requires 2 distinct qualifying seeded clubs, replacing the 3-stint-row and 1-club rules

- **Status:** Accepted (supersedes ADR-0045 Decision §3, and ADR-0047, in full)
- **Date:** 2026-08-17
- **Related requirements:** REQ-1201
- **Related components:** COMP-11 (Games.XGPath)
- **Related decisions:** ADR-0045 (superseded on its §3 "≥3 stint rows"
  point only — §§1-2, §4 unchanged and still authoritative), ADR-0047
  (superseded in full — its 1-club threshold is replaced, but its
  20-appearance-or-unknown bar is carried forward unchanged), ADR-0073
  (BirthYear floor, orthogonal, unaffected by this ADR)

## Context

Epic 12 (docs/backlog.md, S-138) is a continued review of xG Path's
target-player eligibility, following S-137's BirthYear floor. Two existing
rules combine today (`XGPathGameModule.IsEligible`):

1. **ADR-0045 Decision §3:** at least `MinStintCount` (3) documented
   `PlayerCareerStint` *rows*, any clubs — not 3 distinct clubs, since
   `PlayerCareerStint`'s own doc comment allows two rows at the same club
   (a loan, then a later permanent return).
2. **ADR-0047:** at least *one* of those stints at a club in the seeded
   `ClubDefinition` list, with that stint's `AppearanceCount` either
   unknown (`null`) or ≥20 (`MinAppearancesAtSeededClub`).

Both rules are weaker than they look together. A target with exactly one
qualifying seeded-club stint and two throwaway stints at obscure,
unrecognized clubs passes rule 1 trivially and rule 2 on the strength of a
single club — the rest of their documented career contributes nothing to
"a player people have plausibly heard of," the same underlying intent
ADR-0047 was written to protect. Requiring a *second* qualifying seeded
club directly strengthens that signal using data already being fetched
(`ClubDefinition`/`GetClubsAsync()`, `PlayerCareerStint.AppearanceCount`) —
no new external call, no schema change.

## Decision

**xG Path eligibility now requires at least 2 DISTINCT clubs from the
seeded `ClubDefinition` list, each individually meeting ADR-0047's existing
≥20-appearance-or-unknown bar.** `XGPathGameModule.IsEligible` changes
from:

```csharp
if (stints.Count < MinStintCount)
    return false;
// ... date-order check unchanged ...
return stints.Any(s =>
    seededClubNames.Contains(s.ClubName) &&
    (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub));
```

to:

```csharp
// ... date-order check unchanged ...
var qualifyingSeededClubCount = stints
    .Where(s =>
        seededClubNames.Contains(s.ClubName) &&
        (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub))
    .Select(s => s.ClubName)
    .Distinct()
    .Count();
return qualifyingSeededClubCount >= MinQualifyingSeededClubs; // 2
```

**The old `MinStintCount` (≥3 total rows, any clubs) check is dropped
entirely, not kept alongside the new check.** It is now redundant: ≥2
distinct *qualifying* seeded-club stints is a strictly more specific
condition that already implies ≥2 documented rows exist, and nothing in
REQ-1201's text or intent depends on a *third*, unrelated row once the
club-quality bar itself has been raised this way. Keeping both would only
add an unreachable-in-practice extra condition for no benefit.

**The count is over distinct qualifying club NAMES, not stint rows.** Two
stints at the same seeded club (a loan, then a later permanent return)
still count once, not twice — a repeated visit to one big club is not the
same signal as genuine breadth across two. `PlayerCareerStint`'s own
"same club can repeat as separate valid rows" reading (ADR-0045 §3) is
unaffected as a *data* fact; it simply no longer helps satisfy this
specific club-count check on its own.

**The chronological-order-determinable check (ADR-0045 §4) is unchanged**
— this ADR does not touch it. **ADR-0047's 20-appearance-or-unknown bar is
carried forward unchanged**, just now required of two clubs individually
rather than one.

**The narrowing pre-filter
(`IPlayerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync`)**
changes its over-inclusive superset condition from "≥3 total stint rows
AND ≥1 at a seeded club" to "≥2 DISTINCT seeded club names among a
player's stints" (still ignoring the per-club appearance-count
sub-condition, since that only narrows further and the cheap
`(PlayerId, ClubName)` projection this method reads doesn't carry
`AppearanceCount`). This remains a true superset of `IsEligible`'s real
candidates.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the old ≥3-stint-row check alongside the new 2-club check | Smaller diff, "keep everything that isn't proven wrong" | Genuinely redundant once the 2-club check exists (2 qualifying club stints already implies ≥2 rows) — the only extra players it would exclude are, on inspection, ones with exactly 2 documented rows, both at distinct qualifying seeded clubs, which is a real, valid, well-documented short career this codebase has no reason to reject | Adds a dead-weight condition described nowhere in intent, purely for inertia |
| Require a *majority* of stints at seeded clubs, instead of a fixed count of 2 | Scales with career length | Already considered and rejected by ADR-0047 for the same reason: the seeded `ClubDefinition` list is small (~15-30 clubs, `MVP-SCOPE.md`), so most real careers, even star players', are mostly spent outside it | Re-raises ADR-0047's own rejected alternative; nothing about this story changes that reasoning |
| Raise `MinAppearancesAtSeededClub` instead of requiring a second club | Simpler, one-parameter tweak | Doesn't address the actual gap — a single very-well-documented cameo at one big club still says nothing about the rest of a career; the problem is club *breadth*, not per-club threshold strictness | Solves a different problem than the one this story is about |
| Require 2 distinct clubs but drop the individual appearance bar (any stint at 2 seeded clubs, regardless of appearances) | Simpler, fewer combined conditions | Reopens exactly the "one token appearance" loophole ADR-0047 closed, just doubled — 2 fringe cameo appearances at 2 big clubs is not obviously better than 1 real one | ADR-0047's quality bar is still the right per-club filter; this story only changes how many qualifying clubs are required, not whether each one is verified |

## Consequences

- Positive: closes the "one qualifying club carries the whole eligibility
  decision" gap using data already fetched — no new Wikidata query, no
  schema change, no new repository method (only a parameter rename on the
  existing narrowing query).
- Positive: removes a redundant, no-longer-meaningful check
  (`MinStintCount`), simplifying `IsEligible` rather than layering a new
  condition on top of an untouched old one.
- Negative / trade-offs accepted: this is a deliberate narrowing of the
  eligible target-player pool — a real, previously-eligible player whose
  only qualifying seeded-club history is a single big club (however
  well-documented) is now excluded. S-141 (docs/backlog.md) performs the
  same "does the pool stay big enough" empirical verification against real
  data this ADR's own Epic 12 intro requires before this is trusted in
  production, immediately after this lands.
- Follow-up: if the resulting pool proves too small once observed
  (S-141/S-141-adjacent S-136 pool-size check), the number 2 itself, or
  ADR-0047's 20-appearance bar, are the parameters to revisit — not a
  reason to silently reopen the 1-club rule this ADR replaces.

## For AI agents

Do not reintroduce a `MinStintCount`-style "≥N total stint rows, any
clubs" check — it is deliberately removed as redundant, not overlooked.
Do not count qualifying seeded-club stints by stint ROW; count DISTINCT
club NAMES — two rows at the same seeded club (loan, then return) satisfy
only one of the required two clubs, never two. Do not relax
ADR-0047's per-club ≥20-appearance-or-unknown bar when raising the club
count, and do not treat a `null` `AppearanceCount` as disqualifying — both
of ADR-0047's own "For AI agents" points still apply, per-club, under this
ADR's 2-club requirement. Do not touch the chronological-order-determinable
check (ADR-0045 §4) — it is unrelated to this ADR and remains as specified
there. This ADR does not touch ADR-0073's `BirthYear >= 1975` floor, which
remains a separate, additive check evaluated alongside (not inside) the
structural checks this ADR governs.
