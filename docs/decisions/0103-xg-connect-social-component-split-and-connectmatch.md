# ADR-0103: xG Connect: Core.Social as a separate arcade-level component, and ConnectMatch as a new first-class concept (not Round-based)

- **Status:** Accepted
- **Date:** 2026-09-02
- **Related requirements:** REQ-1401, REQ-1402, REQ-1403, REQ-1404, REQ-1405,
  REQ-1406, REQ-1407, REQ-1408, REQ-1409, REQ-1410, REQ-1411
- **Related components:** COMP-16 (Core.Social), COMP-17 (Games.XGConnect),
  COMP-02 (Core.Leagues), COMP-03 (Core.Rounds), COMP-04 (Core.Scoring)

## Context

`docs/requirements-document.md` §4.15 proposes xG Connect, a fourth game:
two players each pick a target player, then race asynchronously to build
the shortest real "played together" chain linking those two targets.
§4.15's own component-boundary note deliberately left two structural
questions open rather than assuming an answer, reserving **COMP-16** and
**COMP-17** in `architecture-document.md` without assigning either a fixed
responsibility:

1. **Component split.** Friend requests, direct challenges, and random
   matchmaking (REQ-1401-1403) are conceptually available to any future
   game — a player's friends list isn't xG-Connect-specific — while
   target-pick selection, chain submission/validation, scoring, match
   resolution, and chat (REQ-1404-1410) are inseparable from xG Connect's
   own rules. Should these be one component, or two — an arcade-level
   social component plus a game module behind `IGameModule` (ADR-0003)?
   REQ-1411 (the cross-cutting notification indicator aggregating pending
   items from both sides) makes this concrete: it needs a clean owner.

2. **Fit with `Round`/`League`.** Every existing game (xG Grid, xG Path, xG
   Predict) plugs into `Core.Rounds` (COMP-03) the same way: a scheduled
   job calls `IGameModule.GenerateInstanceAsync(RoundConfig config)` once
   per `GameKey` on a cron, creating one `Round` shared by every
   participant who plays that `GameKey`, closed and scored as a batch via
   `IScoringStrategy` (COMP-04) into a `FinalPoints` total that
   `Core.Leagues` (COMP-02) ranks. An xG Connect match is structurally
   different on every one of those points: it is created on demand (a
   challenge accepted, or a random pairing forming), scoped to exactly two
   named players rather than every participant of a `GameKey`, and
   resolves to a win/draw/forfeit outcome (REQ-1409), not a points total.
   Does this fit `Round`/`League` as-is, or does it need a new first-class
   concept?

Both questions block every downstream xG Connect story (S-208 onward), so
`docs/backlog.md`'s Epic 27 sequences this ADR first and gates everything
else on it.

## Decision

**(a) Two components, split exactly as `architecture-document.md`
provisionally described them — Core.Social (COMP-16) separate from
Games.XGConnect (COMP-17).**

`Core.Social` (COMP-16) owns `Friendship`/`FriendRequest` (REQ-1401),
`Challenge` (REQ-1402), and `MatchmakingOptIn` (REQ-1403) — arcade-level,
alongside `Core.Users`/`Core.Leagues`, not behind `IGameModule`. It has no
current second caller, but the same reasoning that keeps `Core.Leagues`
(COMP-02) arcade-level rather than folded into a game module applies here:
a friends list is a platform concept a future game could reuse, and
folding it into `Games.XGConnect` would mean any future game wanting
friend-gated challenges depends on xG Connect's own module — the exact
kind of game-to-game coupling the platform-above-games boundary
(`CLAUDE.md`, ADR-0002) exists to prevent. `Games.XGConnect` (COMP-17)
owns target-pick selection, chain submission/validation, scoring, match
resolution, and chat (REQ-1404-1410) behind `IGameModule`, calling
`Core.Social` only to confirm a challenge/pairing has resolved into a
match's two participants — never the reverse.

**REQ-1411's notification indicator belongs to neither** — it is a read
aggregating pending state from both COMP-16 and COMP-17. It is a new
endpoint/service in `XGArcade.Api` (or a small `Core.Notifications`-
adjacent read, COMP-08) that queries both components through their normal
read paths, the same "an aggregating read doesn't need its own owning
component" precedent as any cross-component dashboard-style query
elsewhere in the codebase — not a reason to invent a third component.

**(b) `ConnectMatch` is a new first-class concept, not a `Round`.**

An xG Connect match is created directly by `Core.Social` when a challenge
is accepted or a matchmaking pairing forms (REQ-1402/1403) — never via
`RoundGenerationService`/`IRoundSchedulingOptionsResolver` (COMP-03), and
never assigned a `GameKey`+`GameInstanceId` pair under `Round`. `Games.
XGConnect` persists it as its own entity (`ConnectMatch`, scaffolded in
S-208), scoped to exactly the two participating `UserId`s, carrying its
own win/draw/forfeit outcome (REQ-1409) natively rather than coerced into
`Core.Scoring`'s `FinalPoints`/`IScoringStrategy` shape, which assumes a
directly-comparable points total across every participant of a shared
round. `RoundCloseService`/`LeaderboardService`/`GuessSubmissionService`
are untouched by this decision — they continue to reason only about
`Round`, which xG Connect never creates.

This is a narrower reading of `IGameModule` than every other game module
uses: `Games.XGConnect` does **not** implement the round-generation slice
of `IGameModule` (`GenerateInstanceAsync`, `GetCellIdsAsync`,
`GetMaxAttemptsForCellAsync`) in any meaningful way, since none of it
applies to a game with no `Round`. It still registers as an `IGameModule`
for the one hook that is independent of `Round` — `PurgeUserDataAsync`
(REQ-710, ADR-0101's per-module account-deletion purge loop) — following
the existing precedent of a game module implementing only the subset of
`IGameModule` that applies to it and throwing `NotSupportedException`/
`NotImplementedException` for the rest (COMP-11's `GetCellCategoryTypesAsync`,
COMP-15's `GetMaxAttemptsForCellAsync`). The exact throw-vs-no-op shape for
each inapplicable method is an implementation detail for S-208/S-212 (the
data-model and scoring/resolution stories), not decided further here.

**Explicitly out of scope for this ADR:** whether `ConnectMatch` results
feed the Global League or any custom league's leaderboard at all, and if
so how a win/draw/loss is represented there. `docs/requirements-document.md`
§4.15's own "New (2026-09-02), unresolved" note already flags this as a
genuine product decision, not a technical default — this ADR resolves only
whether xG Connect fits the existing `Round` model structurally (it does
not), not what, if anything, `Core.Leagues` should later do with a
`ConnectMatch` outcome. That remains open, tracked against a future story,
not assumed by S-208 onward.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| One component (COMP-16 folded into COMP-17, e.g. `Games.XGConnect` owns friends/challenges too) | Simplest possible split for a game that is currently the only caller of "friends"; one less component to wire REQ-1411 across | Contradicts the platform-above-games boundary the moment a second game wants friend-gated challenges — would require either duplicating friend logic in a new module or that module depending on `Games.XGConnect` directly, exactly the game-to-game coupling ADR-0002/ADR-0003 exist to prevent; also misreads `requirements-document.md` §4.15's own framing, which explicitly describes friends as available "to any game, not xG-Connect-specific" | Optimizes for today's only caller over the stated platform shape; the arcade-level precedent (`Core.Users`/`Core.Leagues` already sit above every game) is directly analogous and free to reuse |
| Force `ConnectMatch` into the existing `Round` model (one `Round` per match, `GameInstanceId` = the match, generated on-demand rather than by `RoundGenerationService`'s cron) | Reuses `Core.Rounds`/`Core.Leagues`/`Core.Scoring` wiring with no new concept; `LeaderboardService` gets match results "for free" | `Round` has no concept of "for exactly these two named participants" — every existing scope (global/active/closed/windowed) assumes every `GameKey` participant can play any round of that key; `RoundGenerationService`'s per-`GameKey` cron model has no on-demand creation path (`POST /internal/generate-round` is bearer-token/cron-triggered, not player-triggered); win/draw/forfeit doesn't fit `FinalPoints`'s single-comparable-total shape without a lossy or fictional mapping (e.g. inventing points for a win) that the product owner was never asked to confirm | Would silently graft xG Connect's genuinely different shape onto `Round` by force, contradicting ADR-0003's whole point that `Core.Rounds` stays generic — the mismatch is structural, not cosmetic, so a new concept is the honest fit |
| New concept, but owned jointly by `Core.Rounds` (e.g. a `MatchRound` subtype living in COMP-03) rather than entirely inside `Games.XGConnect` | Keeps all round-shaped concepts under one component | `Core.Rounds` would gain game-specific knowledge of xG Connect's two-participant, on-demand creation shape — the same anti-pattern ADR-0003 forbids for a direct FK, just moved to a subtype instead of a column | Rejected for the same reason ADR-0003 rejected a direct FK: `Core.Rounds` must stay ignorant of any specific game's internals |

## Consequences

- Positive: the arcade/game boundary stays exactly where it already is —
  `Core.Social` sits alongside `Core.Users`/`Core.Leagues` as a genuinely
  reusable platform primitive, available to a future fifth game without
  any dependency on `Games.XGConnect`.
- Positive: `Core.Rounds`/`Core.Scoring`/`Core.Leagues` need zero changes
  to support xG Connect — S-208 onward touches only new entities in
  `Games.XGConnect`/`Core.Social` and, for REQ-1411, one small aggregating
  read. This is the same "a second/third game required no `Core.Rounds`
  change" result ADR-0003's own follow-up addendum recorded for xG Path,
  now confirmed a second time for a game that doesn't use `Round` at all.
- Negative / trade-offs accepted: `Games.XGConnect` implements only a
  narrow slice of `IGameModule` — the round-generation methods are
  meaningless for this game and must throw/no-op, mirroring existing
  precedent (COMP-11/COMP-15) rather than inventing a cleaner, smaller
  interface split. If a future game also turns out to be non-`Round`-based,
  this repeated throw/no-op pattern is a signal `IGameModule` itself may
  need splitting into a round-based and a non-round-based contract —
  not done here, since xG Connect is the first and only case so far.
- Negative / trade-offs accepted: xG Connect match outcomes are invisible
  to every existing leaderboard scope until a separate product decision
  resolves the still-open "does a win/draw/loss feed a leaderboard"
  question (`requirements-document.md` §4.15's unresolved note). Players
  will see zero xG Connect presence in `Core.Leagues` at first launch.
- Follow-up: once that leaderboard product decision is made, it needs its
  own ADR if it changes `Core.Leagues`' shape (e.g. a non-`FinalPoints`
  leaderboard scope) — this ADR does not pre-decide it.
- Follow-up: if `IGameModule`'s round-generation methods keep being
  no-ops/throws for non-`Round`-based games beyond xG Connect, revisit
  splitting the interface then, per the trade-off above.

## For AI agents

Do not create a `Round`, `GameKey`/`GameInstanceId` pair, or any
`RoundGenerationService`/`IRoundSchedulingOptionsResolver` wiring for xG
Connect matches — `ConnectMatch` is created directly by `Core.Social` on
challenge-accept or matchmaking-pair, never by the round-scheduling cron.
Do not add a direct dependency from `Core.Social` (COMP-16) to
`Games.XGConnect` (COMP-17) internals, or vice versa beyond COMP-17 reading
COMP-16's normal repository/service interfaces to find a resolved match's
two participants — friends/challenges must stay reusable by a future game
without depending on xG Connect. Do not wire `ConnectMatch` results into
any `Core.Leagues` leaderboard scope without a separate product decision
and, if it changes `Core.Leagues`' shape, its own ADR — this decision
deliberately leaves that open. If a task seems to require any of the
above, stop and flag it rather than working around this ADR.
