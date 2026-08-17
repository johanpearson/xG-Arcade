# ADR-0071: Per-GameKey Round.SequenceNumber as a plain integer

- **Status:** Accepted
- **Date:** 2026-08-17
- **Related requirements:** REQ-304
- **Related components:** COMP-03 (Core.Rounds)

## Context

`Round.Id` is a `Guid`, and it was the only identifier ever exposed to a
human — including, until this ADR, literally as visible text in the admin
panel's round-control section (`RoundControlSection.tsx`). There was no
round-number concept anywhere in the system (`frontend/src/lib/types.ts`
carried a comment explicitly stating this and warning against a fabricated
one). REQ-304 introduces a real, persisted, human-readable label so an
admin can refer to "round #14" instead of a GUID, without touching any of
the existing GUID-based routing/FK wiring (`Round.Id` remains the sole real
identifier for guess/suggestion submission and leaderboard lookups).

The label needs to be: assignable without a second scheduled process (it's
set inline in `RoundGenerationService`, the only place `Round` rows are
created outside test-data seeding), unique and gapless per `GameKey` (xG
Grid and xG Path must not interfere with each other's numbering, matching
`IRoundSchedulingOptionsResolver`'s existing per-`GameKey` independence),
and backfillable for every historical row in one migration.

## Decision

Add `Round.SequenceNumber` (`int`, `required`), computed as
`MAX(SequenceNumber) + 1` scoped to the new row's own `GameKey` (starting
at 1 for that `GameKey`'s first row), read via a new
`IRoundRepository.GetMaxSequenceNumberByGameKeyAsync` and set immediately
before `RoundGenerationService`'s existing `AddAsync` call. A unique index
on `(GameKey, SequenceNumber)` (`XGArcadeDbContext`) is the actual race
guard — the MAX-read and the insert are two separate round-trips, not one
transaction, so two concurrent generation attempts racing that read would
otherwise compute the same next value; the loser's `AddAsync` fails on the
constraint instead of persisting a duplicate. This mirrors REQ-301's own
existing idempotency reasoning ("nothing to do until the upcoming round
becomes active itself") rather than introducing new locking machinery.

The migration that adds the column backfills every existing row by
ordering each `GameKey`'s own rows by `StartTime` ascending and numbering
them 1, 2, 3, ... via a `ROW_NUMBER() OVER (PARTITION BY "GameKey" ORDER
BY "StartTime")` window function, so backfilled history is
indistinguishable from a sequence generated entirely by the
assignment behavior going forward.

`SequenceNumber` is added to every round-shaped DTO
(`CurrentRoundResponse`, `CurrentPathResponse`, `ClosedRoundSummary`/
`ClosedRoundSummaryResponse`, `GenerateRoundResponse`, `AdminRoundResponse`)
alongside the existing `RoundId`, which is never removed, renamed, or
replaced as the routing/FK identifier anywhere.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Plain `int`, `MAX+1` per `GameKey` (chosen) | Simplest possible persisted concept; trivial to backfill; no new formatting/parsing code | Two different `GameKey`s can display the same number (e.g. both show "Round #3") | Acceptable — REQ-304 explicitly requires this independence, matching `IRoundSchedulingOptionsResolver`'s existing per-`GameKey` scoping; the admin UI always labels which game a round number belongs to ("Grid Round #N" / "Path Round #N"), so the shared-number case is never ambiguous in context |
| Formatted code, e.g. `GRID-2026-08-17-01` | Globally unique-looking, encodes the date and game in the label itself | Adds parsing/formatting code and a second failure mode (date/game encoding logic) for zero functional benefit — REQ-304 only ever needs a human-speakable label, never a machine-parsed one | Rejected for complexity disproportionate to the requirement — a plain integer is simpler and does everything asked of it |
| Global (cross-`GameKey`) counter | One number space, no cross-game ambiguity at all | Breaks REQ-304's explicit independence requirement and `IRoundSchedulingOptionsResolver`'s existing per-`GameKey` scoping precedent; a new game module added later would start at whatever number the existing games have already reached, which reads as arbitrary rather than "round 1" | Rejected — contradicts REQ-304's own acceptance criteria |
| Application-level transaction wrapping the MAX-read and the insert | Removes reliance on the unique index catching the race | More machinery (explicit `BeginTransactionAsync`/serializable isolation) for a race window that's already narrow and already safely caught by the unique constraint — the loser's `AddAsync` just fails and is retried on next invocation, same "safe to trigger more often than necessary" property `RoundGenerationService`'s own idempotency check relies on | Deferred — the unique index is a sufficient, much simpler guard for how this method is actually called (a single scheduled job per `GameKey`, not high-concurrency writers) |

## Consequences

- Positive: admins (and any future support/debug tooling) can refer to a
  round by a short number instead of copying a GUID; no existing
  routing/FK code needed to change.
- Negative / trade-offs accepted: the unique index is the real race guard,
  not application-level locking — acceptable given `RoundGenerationService`
  is only ever invoked by one scheduled job per `GameKey` today (ADR-0024),
  but if a second, genuinely concurrent caller of
  `GenerateNextRoundIfNeededAsync` is ever introduced, its `AddAsync`
  failure path needs an explicit retry, which does not exist yet.
- Follow-up: if a second concurrent caller of round generation is added,
  revisit whether the unique-index-as-race-guard is still sufficient or
  needs an explicit retry-on-conflict loop.

## For AI agents

`SequenceNumber` is a display-only label. Never accept it as a route
parameter, request body identifier, or foreign key anywhere — `Round.Id`
(`Guid`) remains the only real identifier for routing, guess/suggestion
submission, and leaderboard lookups. If code you are about to write would
resolve a round by `SequenceNumber` instead of `Id`, stop and flag it
rather than silently adding a second lookup path.
