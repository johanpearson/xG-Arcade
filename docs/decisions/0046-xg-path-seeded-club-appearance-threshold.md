# ADR-0046: xG Path eligibility requires meaningful playing time at the seeded club, not just any stint

- **Status:** Accepted
- **Date:** 2026-07-27
- **Related requirements:** REQ-1201
- **Related components:** COMP-11 (Games.XGPath)

## Context

REQ-1201's original reading (ADR-0045) required only "at least one" of a
candidate's ≥3 documented stints to be at a club in the seeded
`ClubDefinition` list. That's a weak signal: it filters out candidates
with *zero* big-club history, but not a candidate whose only seeded-club
stint was a brief loan or fringe appearance, with the rest of their
documented career at genuinely obscure clubs. Especially now that
REQ-1203 (2026-07-27 revision) reveals *every* documented stint rather
than capping at 5, a target like that would spend most of the puzzle's
clues on clubs nobody recognizes, which undermines "a player people have
plausibly heard of" as the actual intent behind REQ-1201's eligibility
gate.

`PlayerCareerStint.AppearanceCount` (ADR-0042) already carries exactly the
data needed to distinguish "played there" from "played there regularly" —
it's fetched from Wikidata's P1350 qualifier alongside the club/date data
REQ-1201 already reads, so tightening this check needs no new external
call or schema change.

## Decision

A seeded-club stint only counts toward REQ-1201 eligibility if its
`AppearanceCount` is either **unknown** (`null`) or **at least 20**.
`XGPathGameModule.IsEligible` changes from:

```csharp
stints.Any(s => seededClubNames.Contains(s.ClubName))
```

to:

```csharp
stints.Any(s =>
    seededClubNames.Contains(s.ClubName) &&
    (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub))
```

with `MinAppearancesAtSeededClub = 20`.

**Unknown counts pass, not fail.** Wikidata's P1350 qualifier is
frequently absent even for well-known players with substantial careers at
a club — treating "unknown" as "reject" would disqualify real, notable
targets for a data-completeness gap, not for actually having a fringe
career. Only a *known*, sub-threshold count disqualifies a stint, since
that's real evidence of limited playing time.

**20 is a starting point, not a rigorously derived number.** It's picked
as "meaningfully more than a handful of substitute appearances or a short
loan," while still low enough not to exclude a breakout younger player.
Revisit if real seeded data shows it's too strict or too lax once xG Path
ships and target quality can be judged by playing it (the same "revisit
once you can observe it" precedent `MVP-SCOPE.md` already uses for the
Trophy category's introduction trigger).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Leave "at least one stint, any length" (current shipped behavior) | No code change | Doesn't address the actual complaint — a token appearance still qualifies a candidate | Doesn't solve the problem this ADR exists to fix |
| Reject a stint with unknown `AppearanceCount` (treat null as failing the threshold) | Simpler mental model ("no data = no credit") | Wikidata's P1350 is commonly missing for real, well-documented careers — would silently exclude genuinely notable players for a data gap, not a real fringe-career signal | Punishes missing data instead of only punishing known-bad data, the opposite of how REQ-1203 already treats unknown `AppearanceCount` (still shown, not skipped) |
| Add a real league-tier/competition data model and require "top-5-league" specifically | More precise signal than club identity alone | New reference data, new Wikidata query shape or a hand-curated league list — real scope beyond a threshold tweak, and `MVP-SCOPE.md`'s Tier 0 explicitly avoids new external-data plumbing when a cheaper signal from data already fetched will do | Bigger change than the problem currently warrants; revisit if a 20-appearance floor turns out insufficient once observed in practice |
| Require the *majority* of a candidate's stints (not just one) to be at seeded clubs | Stronger notability signal | Seeded `ClubDefinition` is a small (~15 club) curated list (`MVP-SCOPE.md`) — most real careers, even for well-known players, are mostly spent at clubs outside that list; a majority requirement would reject far too many otherwise-good targets | Conflates "seeded-club coverage" with "career length," which aren't the same thing |

## Consequences

- Positive: closes the "one token appearance at a big club" loophole
  REQ-1203's "show every stint" revision made more visible, using data
  already being fetched — no new Wikidata query, no schema change
- Negative / trade-offs accepted: the 20-appearance number is a judgment
  call, not derived from any real distribution of the seeded player pool
  yet; it may need tuning once xG Path is actually playable
- Follow-up: if 20 proves wrong in either direction, adjust
  `MinAppearancesAtSeededClub` directly — this doesn't need a new ADR
  unless the *shape* of the rule (unknown-passes, single-stint check)
  also changes, only a value tweak

## For AI agents

Do not treat a `null` `AppearanceCount` as disqualifying a seeded-club
stint — only a known value below `MinAppearancesAtSeededClub` (20) fails
the check. Do not read this ADR as requiring league/competition-tier data;
that was explicitly considered and deferred as out of scope for the
problem this ADR actually fixes. If a future change wants a stronger
notability signal than appearance count at one seeded club, that likely
needs its own ADR (the league-tier alternative above), not a quiet
reinterpretation of this one.
