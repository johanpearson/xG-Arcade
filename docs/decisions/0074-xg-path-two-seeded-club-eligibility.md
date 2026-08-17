# ADR-0074: xG Path eligibility requires 2 distinct qualifying seeded clubs, replacing the 1-club rule (3-stint-row floor retained, re-justified for REQ-1203)

- **Status:** Accepted (supersedes ADR-0045 Decision §3's textual reasoning, and ADR-0047, in full)
- **Date:** 2026-08-17
- **Related requirements:** REQ-1201, REQ-1203
- **Related components:** COMP-11 (Games.XGPath)
- **Related decisions:** ADR-0045 (superseded on its §3 textual reasoning
  only — the ≥3-total-row floor it establishes is RETAINED under this ADR,
  just re-justified for a different reason; §§1-2, §4 unchanged and still
  authoritative), ADR-0047 (superseded in full — its 1-club threshold is
  replaced, but its 20-appearance-or-unknown bar is carried forward
  unchanged), ADR-0073 (BirthYear floor, orthogonal, unaffected by this ADR)

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

The original S-138 backlog text proposed dropping `MinStintCount` (the
≥3-total-row floor) entirely, reasoning that "≥2 seeded stints is a
strictly more specific requirement that makes the old, weaker, club-blind
count check redundant." **Architecture and quality review of the resulting
diff found that reasoning incomplete: ≥2 distinct qualifying seeded clubs
only implies ≥2 total documented rows, not ≥3.** A real candidate whose
only two documented career stints are both qualifying seeded clubs (no
third row of any kind) would pass the new club-count rule alone.
`PathClueSequenceBuilder.SplitIntoTurns` (REQ-1203) divides a target's full
stint count `N` across exactly 3 fixed club-reveal turns and assumes
`N >= 3` — for `N = 2` it produces turn sizes `[0, 1, 1]`, silently
revealing **zero clubs** on the puzzle's first club-reveal turn, a visible,
broken player-facing bug reachable via the real `/internal/generate-round`
→ `PathEndpoints` display path, not merely theoretical. This ADR's Decision
below reflects that correction: the total-row floor is **retained**, not
dropped, but its justification changes from ADR-0045's original "literal
reading of REQ-1201's text" reasoning (now moot, since REQ-1201's own rule
no longer hinges on that literal "3") to a REQ-1203-specific structural
requirement, independent of REQ-1201's own club-quality signal.

## Decision

**xG Path eligibility now requires BOTH of the following, as independent
conditions:**

1. **At least `MinDocumentedStintCount` (3) total documented
   `PlayerCareerStint` rows, any clubs** — renamed from `MinStintCount`,
   value unchanged at 3, but re-justified: this floor exists purely so
   REQ-1203's `PathClueSequenceBuilder` always has enough documented career
   data to build a real 3-turn club reveal. It is **not** a re-statement of
   ADR-0045's original "3 distinct documented career club stints" textual
   reasoning — that question is now moot.
2. **At least 2 DISTINCT clubs from the seeded `ClubDefinition` list, each
   individually meeting ADR-0047's existing ≥20-appearance-or-unknown
   bar** — the actual REQ-1201 club-quality change this story is about.

`XGPathGameModule.IsEligible` changes from:

```csharp
if (stints.Count < MinStintCount) // 3
    return false;
// ... date-order check unchanged ...
return stints.Any(s =>
    seededClubNames.Contains(s.ClubName) &&
    (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub));
```

to:

```csharp
if (stints.Count < MinDocumentedStintCount) // 3, same value, new name/reason
    return false;
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

**The count in condition 2 is over distinct qualifying club NAMES, not
stint rows.** Two stints at the same seeded club (a loan, then a later
permanent return) still count once, not twice — a repeated visit to one
big club is not the same signal as genuine breadth across two.
`PlayerCareerStint`'s own "same club can repeat as separate valid rows"
reading (ADR-0045 §3) is unaffected as a *data* fact; it simply no longer
helps satisfy this specific club-count condition on its own, and it also
does not, by itself, satisfy condition 1 unless a genuine third row exists.

**The chronological-order-determinable check (ADR-0045 §4) is unchanged**
— this ADR does not touch it. **ADR-0047's 20-appearance-or-unknown bar is
carried forward unchanged**, just now required of two clubs individually
rather than one.

**The narrowing pre-filter
(`IPlayerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync`)**
keeps both conditions as an over-inclusive superset: "≥`minTotalStintCount`
(3) total stint rows AND ≥`minSeededClubCount` (2) DISTINCT seeded club
names among a player's stints" (still ignoring the per-club
appearance-count sub-condition, since that only narrows further and the
cheap `(PlayerId, ClubName)` projection this method reads doesn't carry
`AppearanceCount`). This remains a true superset of `IsEligible`'s real
candidates.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Drop the ≥3-total-row floor entirely once the 2-club check exists (the original S-138 backlog proposal) | Smaller diff, "≥2 qualifying clubs looks strictly more specific" | **Incorrect on inspection**: ≥2 distinct qualifying seeded clubs only implies ≥2 total rows, not ≥3 — a genuine 2-stint candidate (both qualifying seeded clubs, no third row) would pass eligibility and break REQ-1203's `PathClueSequenceBuilder` 3-way turn split (`SplitIntoTurns(2)` → `[0, 1, 1]`, an empty first clue turn) | Found during architecture/quality review to be a real, production-reachable bug, not a theoretical edge case — REQ-1203's dependency on `N >= 3` was overlooked when the backlog story was written |
| Redefine `PathClueSequenceBuilder.SplitIntoTurns` to handle `N < 3` instead of keeping the eligibility floor | Would let a genuine 2-stint career become a valid target | REQ-1203's fixed 3-club-reveal-turn structure is itself a deliberate, documented design (REQ-1203's own worked examples all assume `N >= 3`); redefining it is a REQ-1203 change with its own display/UX implications, well beyond this story's scope (raising REQ-1201's club-quality bar) | Out of scope for S-138; if a future story wants to admit shorter documented careers as targets, REQ-1203's turn-split rule needs its own deliberate redesign and REQ update, not a silent side effect of an eligibility-rule story |
| Require a *majority* of stints at seeded clubs, instead of a fixed count of 2 | Scales with career length | Already considered and rejected by ADR-0047 for the same reason: the seeded `ClubDefinition` list is small (~15-30 clubs, `MVP-SCOPE.md`), so most real careers, even star players', are mostly spent outside it | Re-raises ADR-0047's own rejected alternative; nothing about this story changes that reasoning |
| Raise `MinAppearancesAtSeededClub` instead of requiring a second club | Simpler, one-parameter tweak | Doesn't address the actual gap — a single very-well-documented cameo at one big club still says nothing about the rest of a career; the problem is club *breadth*, not per-club threshold strictness | Solves a different problem than the one this story is about |
| Require 2 distinct clubs but drop the individual appearance bar (any stint at 2 seeded clubs, regardless of appearances) | Simpler, fewer combined conditions | Reopens exactly the "one token appearance" loophole ADR-0047 closed, just doubled — 2 fringe cameo appearances at 2 big clubs is not obviously better than 1 real one | ADR-0047's quality bar is still the right per-club filter; this story only changes how many qualifying clubs are required, not whether each one is verified |

## Consequences

- Positive: closes the "one qualifying club carries the whole eligibility
  decision" gap using data already fetched — no new Wikidata query, no
  schema change, no new repository method (only a parameter rename/addition
  on the existing narrowing query).
- Positive: the ≥3-total-row floor's purpose is now explicit and correctly
  attributed to REQ-1203 rather than resting on ADR-0045's original,
  now-superseded textual reading of REQ-1201 — a future reader will not
  mistake it for a leftover of the old rule.
- Negative / trade-offs accepted: this is a deliberate narrowing of the
  eligible target-player pool — a real, previously-eligible player whose
  only qualifying seeded-club history is a single big club (however
  well-documented) is now excluded, and a player with exactly 2 total
  documented stints (even at 2 qualifying seeded clubs) remains excluded
  too, for REQ-1203's sake. S-141 (docs/backlog.md) performs the same "does
  the pool stay big enough" empirical verification against real data this
  ADR's own Epic 12 intro requires before this is trusted in production,
  immediately after this lands.
- Follow-up: if the resulting pool proves too small once observed
  (S-141/S-141-adjacent S-136 pool-size check), the number 2 (club count)
  or ADR-0047's 20-appearance bar are the parameters to revisit first — the
  3-total-row floor is a REQ-1203 structural requirement, not a tuning
  knob, and should not be the first thing loosened without also revisiting
  REQ-1203's turn-split design.

## For AI agents

Do NOT drop `MinDocumentedStintCount` (the ≥3-total-row floor,
formerly named `MinStintCount`) — it is required by REQ-1203's
`PathClueSequenceBuilder`, independently of `MinQualifyingSeededClubs`
below, and dropping it reintroduces a real, production-reachable bug (an
empty first club-reveal turn for a 2-stint target). Do not conflate the
two conditions: `MinDocumentedStintCount` counts total stint ROWS, any
clubs; `MinQualifyingSeededClubs` counts DISTINCT qualifying seeded club
NAMES — two rows at the same seeded club (loan, then return) satisfy only
one of the required two clubs, never two, and also do not by themselves
satisfy the row-count floor unless a genuine third row exists. Do not
relax ADR-0047's per-club ≥20-appearance-or-unknown bar when raising the
club count, and do not treat a `null` `AppearanceCount` as disqualifying —
both of ADR-0047's own "For AI agents" points still apply, per-club, under
this ADR's 2-club requirement. Do not touch the
chronological-order-determinable check (ADR-0045 §4) — it is unrelated to
this ADR and remains as specified there. This ADR does not touch
ADR-0073's `BirthYear >= 1975` floor, which remains a separate, additive
check evaluated alongside (not inside) the structural checks this ADR
governs.
