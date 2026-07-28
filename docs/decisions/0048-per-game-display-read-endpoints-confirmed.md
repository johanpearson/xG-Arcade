# ADR-0048: ADR-0016's direct-repository-read pattern is confirmed for a second game module, not superseded by a generic IGameModule read method

- **Status:** Accepted
- **Date:** 2026-07-27
- **Related requirements:** REQ-1203
- **Related components:** COMP-03 (Core.Rounds), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

ADR-0016 permitted `RoundEndpoints.cs`'s `GET /rounds/current` to read
`GridInstance`/`GridCell` directly via `IGridInstanceRepository`, bypassing
`IGameModule`, for read-only display queries specifically — never for
generation or scoring. It did so as a deliberate, narrow, Tier-0-scoped
trade-off, explicitly declining to design a generic `IGameModule` read
method with only one game module's instance shape (`GridInstance`/
`GridCell`) to generalize from. Its own **Follow-up** section named the
exact trigger for revisiting that choice: "when a second game module is
actually built, use it to design `IGameModule`'s read method for real,
informed by both games' actual instance shapes — supersede this ADR at
that point rather than letting the direct-repository-read pattern spread
to more endpoints."

That trigger has now fired for real. S-082 (REQ-1203, xG Path's clue-reveal
read path) added `GET /path/current` (`PathEndpoints.cs`), which reads
`PathInstance`/`PathPuzzle` directly via `IPathInstanceRepository`, the same
shape `RoundEndpoints.cs` already uses for `GridInstance`/`GridCell`. Two
real, structurally different instance shapes now exist to compare:

- `GridInstance`/`GridCell`: an N×N grid of cells, each with row/column
  category type/value pairs, no single fixed "answer" per cell (multiple
  players can satisfy a cell), REQ-204's live uniqueness percentage
  computed per correct guess.
- `PathInstance`/`PathPuzzle`: a flat list of puzzles, each with exactly one
  fixed target player, a 7-turn progressive clue-reveal sequence
  (`PathClueSequenceBuilder`) with no category concept and no uniqueness
  score at all (ADR-0040).

`architecture-reviewer` reviewed both shapes side by side during S-082's
quality gate and recommended confirming the existing per-game
direct-repository-read pattern as the accepted long-term shape, rather than
now attempting to design a generic `IGameModule` read method — the same
"don't build the generalized interface until a second real data point
exists" reasoning ADR-0016 (and, before it, ADR-0003's own follow-up note)
already used, now actually validated against real data instead of a
hypothetical. The two shapes above share almost no structure a generic
method could usefully abstract over without either leaking one game's
vocabulary into `Core.Games` (ADR-0016's own objection to this, restated)
or degrading to an untyped `object` return the Api layer would still have
to downcast per game — closing no real coupling, just relocating it.
`MVP-SCOPE.md`'s explicit bias against premature abstraction at Tier 0
applies directly here: a two-case pattern with a clean, working
per-endpoint shape is not evidence a shared abstraction is overdue.

## Decision

ADR-0016's direct-repository-read pattern for read-only, non-scoring
display endpoints is confirmed as the platform's accepted long-term shape,
not a Tier-0-only stopgap awaiting a generalized `IGameModule` read method.
`RoundEndpoints.cs`'s `GET /rounds/current` and `PathEndpoints.cs`'s
`GET /path/current` are both correct, permanent examples of this pattern,
not two instances of technical debt to be unified later by default. A
future third game module's own equivalent display-read endpoint should
follow the same pattern (its own repository interface, read directly from
the Api layer) rather than waiting on this ADR to be revisited again.

This does not reopen ADR-0016's own scope: it still applies only to
read-only, non-scoring queries against an already-generated instance, never
to generation (`GenerateInstanceAsync`) or scoring (`ScoreSubmissionAsync`),
which remain the only two paths through `IGameModule`.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Design a generic `IGameModule` read/view method now, informed by both `GridInstance`/`GridCell` and `PathInstance`/`PathPuzzle`'s real shapes | Closes ADR-0016's own follow-up as originally framed; fully decouples the Api layer from any one game's instance shape | The two real shapes share almost no structure to generalize over (categories/cells vs. flat targets/clue turns) — a shared interface method would either leak game-specific vocabulary into `Core.Games` or return an untyped `object` the Api layer downcasts per game anyway, which is the same coupling relocated, not removed; forces a 3-game-shaped abstraction from 2 real data points | Rejected — the two shapes just examined don't support a clean generalization, and guessing at a third game's shape to force one now is the exact premature abstraction ADR-0016/ADR-0003/`MVP-SCOPE.md` all warn against |
| Leave ADR-0016 as a standing "revisit every time a new game module lands" open question | No new ADR needed right now | Re-litigates the same question on every future game module instead of answering it once with real evidence; leaves `PathEndpoints.cs`'s own in-code "flagged for architecture-reviewer" comment permanently unresolved | Rejected — the trigger ADR-0016 named has now fired and been evaluated; leaving the question open after evaluating it serves no one |
| Confirm the existing pattern as accepted, close ADR-0016's follow-up (chosen) | Matches what was actually found by comparing two real shapes; no interface designed against a guess; keeps both existing endpoints as-is (no code change required); documents the decision so it isn't re-litigated per future game module | A third game module with genuinely shared display-read needs would still duplicate some read-composition logic across its own endpoint — accepted as a real but currently hypothetical cost, revisitable if it actually materializes | Accepted — evidence-based, no speculative interface, consistent with this codebase's established "flag and defer until real data exists, then decide" pattern |

## Consequences

- Positive: `PathEndpoints.cs`'s own in-code "flagged for architecture-reviewer, not resolved here" comment is now resolved — no code change required, since the existing pattern was already correct; future game modules have a clear, confirmed precedent to follow for their own display-read endpoints without re-opening this question; ADR-0016's follow-up trigger is closed rather than left permanently pending
- Negative / trade-offs accepted: the Api layer remains coupled to each game module's own instance shape for display reads (`RoundEndpoints.cs` to `GridInstance`/`GridCell`, `PathEndpoints.cs` to `PathInstance`/`PathPuzzle`) — this is now a permanent, accepted shape rather than a temporary one; a third game module could reveal enough shared structure to make a generic read method worthwhile after all, which this ADR does not rule out, only declines to guess at now
- Follow-up: if a third game module's display-read needs turn out to share real, non-coincidental structure with the two examined here, revisit this ADR informed by three real shapes rather than two — the same "wait for real data" discipline this ADR itself just applied

## For AI agents

`GET /rounds/current` and `GET /path/current`'s direct-repository-read
pattern is the accepted, permanent shape for read-only display endpoints —
do not treat either as technical debt to "fix" by building a generic
`IGameModule` read method, and do not flag a new game module's equivalent
endpoint as an ADR-0016 violation requiring escalation. It still does not
extend to generation or scoring, which must always go through
`IGameModule`. If a third game module's own display-read endpoint reveals
real shared structure with these two, propose a superseding ADR informed by
that concrete evidence rather than building a speculative abstraction
first.
