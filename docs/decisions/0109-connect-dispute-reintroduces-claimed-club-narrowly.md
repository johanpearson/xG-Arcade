# ADR-0109: xG Connect's dispute flow reintroduces a player-claimed club, narrowly

- **Status:** Accepted
- **Date:** 2026-09-05
- **Related requirements:** REQ-1412, REQ-1413, REQ-1414
- **Related components:** COMP-17 (Games.XGConnect)

## Context

ADR-0104 removed xG Connect's player-typed "claimed club" field entirely,
replacing it with server-side auto-detection (`GetSharedClubOverlapsAsync`)
for every ordinary chain-step submission. That ADR's own "For AI agents"
section is explicit: reintroducing a player-facing club-name input needs a
new ADR reasoning about why the auto-detect design is being reversed, not
just a REQ or a bug fix.

The product owner has now designed a new feature (REQ-1412/1413/1414): when
a chain-step submission fails auto-detection, the player may dispute that
ruling by naming the specific club they believe the two players share,
rather than accepting the retry/forfeit outcome REQ-1407 would otherwise
apply. If the match's own opponent later approves the dispute, the claimed
club is accepted as fact and the step counts; if denied, the player is
busted. This directly reintroduces a player-typed club-name input — exactly
what ADR-0104 removed — so it needs its own reasoning, per that ADR's own
rule, before implementation proceeds.

The reasoning is genuinely different from ADR-0104's original problem.
ADR-0104 removed the claimed-club field because it was compared
server-side, by string, against `PlayerCareerStint.ClubName` — and that
comparison kept producing false rejections for natural, correct input
(legal suffixes, and an open-ended set of colloquial/abbreviated names
ADR-0104 explicitly flagged as unfixable by string normalization alone).
The dispute flow this ADR covers does no such comparison: the claimed club
is never checked against Wikidata-derived data at all. The opponent's own
human approval is what confirms it, with zero server-side matching — so
ADR-0104's exact failure mode (a correct answer typed in an unexpected
shape) cannot recur here, because there is no string comparison for it to
recur in.

## Decision

Add a claimed-club text input to xG Connect, but ONLY on the
dispute-a-failure flow (REQ-1412) — never on ordinary chain-step
submission, which is untouched and still works exactly as REQ-1406/ADR-0104
already describe. A disputed step's claimed club has no automated
validation whatsoever; it takes effect only once the match's own opponent
explicitly approves it (REQ-1413), and until then it is held as a
Pending, provisional value with no permanent effect. This is the opponent's
word being trusted, not the system's data being re-checked.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Reintroduce the claimed-club input scoped only to the dispute flow, with no automated validation (chosen) | Structurally cannot reproduce ADR-0104's string-matching failure mode, since there is no string match at all; gives players a real recovery path for the exact class of bug this session's ADR-0105 through ADR-0108 chain kept finding (real connections the Wikidata-derived data doesn't yet capture) | Trusts human judgment (the opponent) with no system-side check at all | The whole premise of a dispute is that the system's own data is suspected incomplete or wrong — re-running the same check that already failed, or any variant of it, would defeat the purpose |
| Re-run `GetSharedClubOverlapsAsync` (ADR-0104's auto-detect check) against the claimed club before allowing a dispute to be raised at all | Reuses existing, already-tested logic; zero new server-side surface | Definitionally useless here: if auto-detect already found a match for this pair, the step would not have failed validation in the first place, so this check can never pass on a step that reached the dispute flow | Rejected — it's not a weaker version of validation, it's validation that has already necessarily failed by the time a dispute exists |
| Require the claimed club to at least be a club the candidate has SOME `PlayerCareerStint` row for, even if the overlap window doesn't match | Slightly narrows what a player can falsely claim | Still partially re-trusts the very data suspected of being incomplete (a missing stint row is exactly the kind of gap REQ-1412 exists to route around) for no real protection — a dishonest claim can still name a club the candidate is on record for | Adds complexity against a threat model (a colluding/careless opponent) that this ADR's own Consequences section already accepts as the real, cheaper mitigation boundary |
| Admin-only review of every disputed step, no player-vs-player review | Removes the "opponent could rubber-stamp a false claim" risk entirely | An async, human-admin-reviewed queue cannot resolve fast enough for a match that needs to actually finish; also duplicates REQ-1414's separate, deliberately slower, future-proofing admin queue, which exists precisely because it is NOT meant to gate any live match | Rejected as a substitute for in-match resolution — REQ-1413's opponent review and REQ-1414's admin suggestion queue solve two different problems (this match's own outcome vs. future data quality) and neither can stand in for the other |

## Consequences

- Positive: closes exactly the class of bug this session's ADR-0105 through
  ADR-0108 chain repeatedly found and fixed (a genuine, real "played
  together" connection that the Wikidata-derived data doesn't yet
  capture) — but with a human-judgment escape hatch available immediately,
  in-match, instead of waiting on another data-parsing fix landing in
  production.
- Positive: cannot reproduce ADR-0104's own failure mode, since the dispute
  flow performs no string comparison of the claimed club against anything —
  there is nothing for a legal-suffix or colloquial-name mismatch to break.
- Negative / trade-off accepted: a colluding or simply careless opponent
  could approve a false claim, since there is deliberately no server-side
  check on the claimed club. Accepted because REQ-1413 scopes the blast
  radius to the two players' own match only (never any other match, past or
  future), and REQ-1414's separate, slower, human-admin-reviewed suggestion
  queue is the actual gate before anything about the underlying shared
  player data changes as a result.
- Follow-up: none planned. If dispute abuse (false claims rubber-stamped by
  a complicit opponent) turns out to be a real, observed problem, that is a
  new product decision to make with real data in hand, not something to
  guess at and design against now.

## For AI agents

This ADR authorizes a player-typed club-name input ONLY on the REQ-1412
dispute-a-failure flow. Do not extend it to ordinary chain-step submission,
and do not add any server-side comparison of the claimed club against
`PlayerCareerStint` or any other career data — the opponent's approval
(REQ-1413) is the only check this flow has, by design. If a future change
needs to add validation here, that is itself a new decision reopening this
one, not a bug fix.
