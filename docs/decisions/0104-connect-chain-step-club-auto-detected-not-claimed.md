# ADR-0104: xG Connect chain steps no longer ask the player to name a club

- **Status:** Accepted
- **Date:** 2026-09-04
- **Related requirements:** REQ-1406, REQ-1407
- **Related components:** COMP-17 (Games.XGConnect)

## Context

REQ-1406's original design (S-213) had a player submit two pieces of
information per chain step: a candidate player name, and the specific club
they claimed connected that candidate to the preceding chain player.
`ConnectChainStepService.SubmitChainStepAsync` validated the claim with
`IPlayerCareerOverlapService.HaveOverlapAtClubAsync(candidateId,
precedingPlayerId, claimedClubName)` — an exact, case-insensitive string
comparison between the player-typed club name and the already-canonicalized
`PlayerCareerStint.ClubName` persisted at Wikidata ingest time.

A product-owner bug report (2026-09-04) surfaced that this design was
actively producing false rejections: typing a club's full/legal name (e.g.
"Chelsea FC") failed to match the ingest-time-canonicalized stored value
("Chelsea", since `ClubDefinition`'s own seeded name has no suffix) — a
completely natural thing for a player to type, not a misspelling or a
factual error. A same-day investigation and fix (`ClubNameNormalizer`,
applying suffix-stripping symmetrically to both sides of the comparison)
closed that specific mismatch, but the product owner, discussing the fix,
raised the more fundamental question: why does the player need to type the
club at all? The game already knows which two players are being connected
the moment a candidate is chosen — the club is a fact the system can derive
and confirm, not information only the player has. The product owner's
original intent in requiring a claimed club (REQ-1406's initial design) was
that recalling *where* two players overlapped, not just *that* they did,
should be part of the challenge — but weighed against the fix now being live
and multiple genuine false-rejection reports in one session, the product
owner judged the auto-detect design a clearly easier implementation to get
right and to validate than continuing to patch string-matching edge cases,
and chose it directly.

## Decision

`ConnectChainStepService.SubmitChainStepAsync` no longer takes a
`claimedClubName` parameter. `IPlayerCareerOverlapService` gained
`GetSharedClubOverlapsAsync(playerAId, playerBId)`, returning every club
(and its overlapping year range) the two players actually share — an empty
list means "never played together." `HaveOverlapAtClubAsync` is removed
(no longer called anywhere); `HaveSharedClubOverlapAsync` becomes a thin
`.Count > 0` wrapper over the new method rather than a separate
implementation, so the two can never disagree.

When the returned list is non-empty, the step is accepted; when a pair
shares more than one club (e.g. Maxwell and Zlatan Ibrahimović — Inter,
Barcelona, PSG), `ConnectChainStepService` deterministically persists ONE
representative overlap (the one with the latest `OverlapStartYear`) — same
"pick deterministically rather than invent a new disambiguation mechanism"
precedent this method already established for a same-name candidate
collision. `ConnectChainStep.ClaimedClubName` (required `string`) is
replaced by `MatchedClubName`/`MatchedOverlapStartYear`/
`MatchedOverlapEndYear` (all nullable — null together only when the step is
invalid, since nothing was found). `ChainBuilder.tsx`'s free-text "Claimed
shared club" input is removed entirely; the player submits only a candidate
name, and the accepted-step feedback and `ChainStepsList.tsx`'s historical
render both show the server-computed club and year range instead.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the claimed-club design, just fix the string-matching bug (the same-session fix this ADR supersedes) | Smallest possible change; preserves "recall where, not just who" as part of the challenge | Already-live and still only closes ONE mismatch shape (legal-suffix stripping) — colloquial/abbreviated names ("West Brom" for "West Bromwich Albion") are a different, unbounded matching problem with no clean fix; every future variant is a new potential false-rejection bug | The product owner judged the ongoing string-matching risk not worth preserving a "recall the club" mechanic that was never confirmed as an intentional design goal, once discussing the alternative directly |
| Frontend autocomplete/dropdown for the club field, sourced from a shared canonical list | Keeps the "type/pick a club" interaction; a dropdown can't submit a string that fails to match | Needs a new canonical club-name index or endpoint scoped to the two specific players (or a much larger global one); more moving parts than the check the game already runs to validate the step at all | Auto-detection needs none of that — the same "do these two players share a club" computation the backend already performs (`HaveSharedClubOverlapAsync`, used for the closing-step check since S-213) is sufficient; building a second, UI-facing club-lookup path duplicates it for no added correctness |
| Auto-detect but show the player only a boolean ("valid"/"invalid"), never the actual club/years | Marginally less backend response surface | Throws away genuinely useful, already-computed information (which real club, which years) that both the immediate submit feedback and the persisted chain's own display can use for free | No reason to withhold a fact the server already has to compute anyway |

## Consequences

- Positive: the entire class of "player-typed club string doesn't match the
  canonical stored form" bugs (legal suffixes, colloquial names, casing,
  whitespace — anything) is now structurally impossible, not just patched
  for the one shape already observed.
- Positive: `ChainBuilder.tsx` has one fewer required input, and the
  post-submit feedback/persisted chain display now show genuinely more
  informative data (the real club AND years) than a player-typed claim
  ever guaranteed.
- Positive: reuses `HaveSharedClubOverlapAsync`'s exact underlying
  fetch/cache/live-refresh machinery (`LoadBothPlayersStintsAsync`) — no
  new data path, no new Wikidata query shape.
- Negative / trade-off accepted: recalling *where* two players overlapped
  is no longer part of the challenge, only *whether* a valid connecting
  player exists — a deliberate, explicit product-owner call, not an
  accidental simplification.
- Negative / trade-off accepted: when a pair shares multiple clubs, only
  one (the most recent) is shown/persisted per step — a real, if minor,
  loss of detail for a case like Maxwell/Ibrahimović. Not addressed here;
  revisit only if this turns out to matter in practice (e.g. widening
  `ConnectChainStep` to store every overlap, not just one).
- Follow-up: none planned. `ClubNameNormalizer` (introduced in the
  same-session fix this ADR supersedes) remains in place and still used —
  it stays the ingest-time canonicalization normalizer
  (`SparqlResponseParsers`), just no longer also applied to a player-typed
  value, since no player-typed club value exists anymore.

## For AI agents

Do not reintroduce a player-facing club-name input for xG Connect chain
steps without a new ADR — this decision was made directly by the product
owner after weighing the alternative (keep-and-fix string matching)
explicitly. If a future story wants "recall where you overlapped" back as
part of the challenge, that is a new product decision, not a bug fix, and
needs its own ADR reasoning about why the auto-detect design (this ADR) is
being reversed. `IPlayerCareerOverlapService.GetSharedClubOverlapsAsync` is
the one place interval-overlap math is computed for this component —
`HaveSharedClubOverlapAsync` must stay a thin wrapper over it, never a
second, separately-maintained implementation.
