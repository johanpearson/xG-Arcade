# ADR-0098: REQ-1306's per-player confirm-lock — API-layer gate, own table

- **Status:** Accepted
- **Date:** 2026-08-31
- **Related requirements:** REQ-1302, REQ-1303, REQ-1306
- **Related components:** COMP-15 (Games.XGPredict)

## Context

REQ-1302 (score prediction submission) and REQ-1303 (round-wide automatic
lock) were implemented at the `XGPredictGameModule.ScoreSubmissionAsync`
level only (ADR-0096) — no HTTP endpoint existed. REQ-1306 (an explicit,
optional, per-player "confirm and lock" action, independent of REQ-1303's
round-wide lock) had zero code at all. This story builds the first real
HTTP surface for xG Predict gameplay (`GET /predict/current`,
`POST /predict/matches/{matchId}/predictions`, `POST /predict/confirm`) and,
with it, must decide two things ADR-0096 explicitly left open:

1. **Where does REQ-1306's per-player lock check live?** `XGPredictGameModule.
   ScoreSubmissionAsync` already enforces REQ-1303's round-wide lock
   internally (comparing `TimeProvider` against `Matches.Min(KickoffUtc)`).
   REQ-1306 is a *different*, per-player concept layered on top — a player
   can be individually locked while the round itself is still open for
   everyone else. Enforcing it requires knowing "has this (instance, user)
   pair confirmed?", a piece of state `ScoreSubmissionAsync`'s existing
   signature (`instanceId, userId, submission`) has everything it needs to
   check itself, so both "inside the game module" and "in the calling API
   endpoint" are live options.
2. **Where is the per-player lock persisted?** ADR-0096's own "For AI
   agents" section left a breadcrumb: "If a future change needs
   `PredictMatchPrediction` to carry more per-user state (e.g. a 'confirmed
   and locked' flag for REQ-1306), add it there." That breadcrumb is one
   option, not a binding decision — this ADR is what actually decides it,
   now that a real caller exists.

## Decision

**1. The per-player lock check lives in the API endpoint
(`XGArcade.Api.Predict.PredictEndpoints`'s
`POST /predict/matches/{matchId}/predictions`), not inside
`XGPredictGameModule.ScoreSubmissionAsync`.** The endpoint calls
`IPredictInstanceRepository.IsPlayerLockedAsync` and returns 409 *before*
ever calling `ScoreSubmissionAsync`. Reasoning:

- `IGameModule.ScoreSubmissionAsync`'s contract is shared across every game
  (Grid/Path/Predict) and is deliberately narrow: validate the submission
  shape, apply the game's own scoring/lock rules, persist, return a
  `ScoreResult`. REQ-1306 is not a rule about *this game's scoring* — it's a
  player-initiated, opt-in UX affordance ("give me a sense of closure") that
  happens to gate future writes. Grid/Path have no equivalent concept, and
  nothing in `IGameModule`'s interface expects one; adding it to
  `XGPredictGameModule` would make that one implementation's internal
  behavior depend on a concept the interface itself doesn't model, for no
  benefit no other caller of `ScoreSubmissionAsync` would ever need.
- `ScoreSubmissionAsync` is also called by nothing else in the round-close/
  grading path (`ScoreLockingService`, `PredictGradingService`) — REQ-1306
  only ever needs to be checked at the one write path that can mutate a
  prediction, `POST /predict/matches/{matchId}/predictions`, which is
  exactly where the endpoint layer already sits.
- Keeping it here means `XGPredictGameModule`'s own unit tests
  (`XGPredictGameModuleTests`) stay focused on REQ-1301/1302/1303 exactly as
  ADR-0096 scoped them, and the new per-player-lock tests
  (`PredictEndpointTests`) sit alongside the endpoint they actually gate,
  the same "test at the layer the behavior lives" precedent this codebase
  already follows for `GuessEndpoints`' own outcome-specific rejections
  (REQ-202).

**2. The per-player lock is a new, small table, `PredictPlayerLock`,
composite-keyed on `(PredictInstanceId, UserId)`** — not a column added to
`PredictMatchPrediction`, despite ADR-0096's breadcrumb suggesting that
shape. Reasoning for departing from it:

- REQ-1306 locks a *player's whole round* (all of an instance's matches),
  not one match. A boolean column on `PredictMatchPrediction` would need to
  be set identically across every one of that player's rows for the
  instance (today, 5 of them) to mean the same thing a single
  `PredictPlayerLock` row means — that's redundant storage of one fact,
  repeated N times, with no natural single place to read "is this player
  locked?" without an aggregate query (`ALL(...) == true`) every read site
  would have to get right identically.
- `PredictPlayerLock`'s existence-is-the-lock shape (no boolean to flip, see
  that entity's own doc comment) means there's nothing to keep consistent
  across rows, and a future change to `PredictTemplate.MatchCount` (already
  a stored config value per ADR-0096 §1, even though only 5 is valid today)
  doesn't change this table's shape at all — a column-per-prediction-row
  design would need every one of an arbitrary N rows written together
  anyway, no simpler than a single row.
- This still honors ADR-0096's actual constraint ("repository-per-component,
  `IPredictInstanceRepository` owns all of it, don't invent a second
  repository interface") — the new table's two methods
  (`IsPlayerLockedAsync`/`LockPlayerPredictionsAsync`) are added to the
  existing `IPredictInstanceRepository`/`PredictInstanceRepository`, not a
  new interface.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Check REQ-1306's lock inside `XGPredictGameModule.ScoreSubmissionAsync` (widen its dependencies to include the lock read, or add a new `IGameModule` capability) | One fewer call site duplicating "check before calling"; keeps all of xG Predict's submission rules in one method | Bakes a per-game, opt-in UX concept into a shared cross-game interface method whose contract every other `IGameModule` implementation (and its own tests) has to keep NOT caring about; no other caller of `ScoreSubmissionAsync` (grading, round-close) needs this check, so it would be dead weight on every call path except the one HTTP endpoint | The check has exactly one real caller (this endpoint) and no reason to live one layer further in |
| A boolean `Locked` column on `PredictMatchPrediction`, set on all of an instance's rows for a user at once (ADR-0096's breadcrumb) | Reuses an existing table; matches ADR-0096's own suggested extension point | Denormalizes the same per-player fact across N rows (today 5); requires an all-rows write inside the same confirm action anyway (no simpler than inserting one new row); reading "is this player locked" still requires an aggregate/join query rather than a direct-key lookup | A single row keyed on `(PredictInstanceId, UserId)` is strictly simpler to write and read for a fact that is inherently about the (instance, player) pair, not about any one match |
| A single mutable boolean flag, toggleable back to `false` (supporting a future "let me un-confirm" feature) | More flexible if a future requirement needs it | REQ-1306's own acceptance criteria are explicit: "further edits... are rejected from that point on... independent of... the round-wide automatic lock" — there is no un-confirm behavior specified, and speculatively building one is exactly the kind of scope creep `MVP-SCOPE.md`/CLAUDE.md ask to avoid | Build to the requirement that exists; a mutable flag can be added later (a new migration) if REQ-1306 is ever amended to allow it |

## Consequences

- Positive: `XGPredictGameModule`/`ScoreSubmissionAsync` and its existing
  ADR-0096 test suite are completely unmodified by this story — REQ-1306's
  entire implementation is additive (one new table, two new repository
  methods, one new endpoint file).
- Positive: `PredictPlayerLock`'s existence-is-the-lock shape needs no
  "what does `Locked = false` even look like" edge case — a missing row and
  `Locked = false` are the same state by construction, so there's no way for
  the flag to be present-but-false.
- Negative / trade-off accepted: the round-wide lock (`Locked`, REQ-1303)
  and the per-player lock (`ConfirmedLocked`, REQ-1306) are two independent
  reads (`PredictInstance.LockInstant` vs. `IsPlayerLockedAsync` —
  quality-gate fix, 2026-08-31: `LockInstant` is now a shared `[NotMapped]`
  computed property on `PredictInstance`, extracted after this formula was
  independently re-derived at three call sites) that a caller must remember
  to check separately — there is no single "is this round/player still
  editable" helper. Acceptable for now (only one caller, `PredictEndpoints`,
  needs either check); revisit if a third call site needs the same combined
  check.
- **Risk flagged (architecture-review, 2026-08-31): this decision's Decision
  §1 reasoning — "the check has exactly one real caller" — depends on
  `GuessEndpoints`/`GuessSubmissionService` never becoming a second path
  into `XGPredictGameModule.ScoreSubmissionAsync`.** Today `GuessEndpoints`'
  `POST /rounds/{roundId}/cells/{cellId}/guesses` has no `GameKey`
  allow-list — if called against an active xG Predict round, it currently
  fails safely before reaching `ScoreSubmissionAsync` (`GuessSubmissionService`
  first calls `GetMaxAttemptsForCellAsync`, which `XGPredictGameModule` still
  throws `NotImplementedException` for), so this ADR's lock cannot be
  bypassed today. But that safety is incidental, not structural: the moment
  `GetMaxAttemptsForCellAsync` is implemented for xG Predict (see that
  method's own TODO) without also gating `GuessEndpoints`/
  `GuessSubmissionService` by `GameKey`, `ScoreSubmissionAsync` would become
  reachable from a second, unguarded path that never checks
  `IsPlayerLockedAsync`. Whoever implements `GetMaxAttemptsForCellAsync` for
  this game must either add an explicit `GameKey` guard to
  `GuessEndpoints`/`GuessSubmissionService`, or move REQ-1306's check
  somewhere both paths pass through — do not implement that method without
  addressing this.
- Follow-up: if a future requirement needs "how many players in this round
  have confirmed" (e.g. an admin/ops view), `PredictPlayerLock` already
  supports a `COUNT(*) WHERE PredictInstanceId = ...` query with no schema
  change — flagged here so a future agent doesn't reach for a denormalized
  counter instead.

## For AI agents

Do not add REQ-1306's lock check to `XGPredictGameModule.ScoreSubmissionAsync`
— it belongs in `XGArcade.Api.Predict.PredictEndpoints`, checked before
`ScoreSubmissionAsync` is called, per Decision §1 above. Do not add a
`Locked`/`Confirmed` column to `PredictMatchPrediction` to represent this —
use `PredictPlayerLock` (`IPredictInstanceRepository.IsPlayerLockedAsync`/
`LockPlayerPredictionsAsync`), per Decision §2 above; this explicitly
supersedes ADR-0096's own "For AI agents" breadcrumb suggesting a
`PredictMatchPrediction` column, now that a real caller has decided the
actual shape. Never make this lock mutable back to "unlocked" without a new
REQ/ADR — REQ-1306 as written has no un-confirm behavior.
