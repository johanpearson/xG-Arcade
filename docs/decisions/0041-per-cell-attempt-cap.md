# ADR-0041: Guess attempt cap becomes per-cell, not a single global constant

- **Status:** Accepted
- **Date:** 2026-07-26
- **Related requirements:** REQ-210, REQ-1201-REQ-1206 (xG Path)
- **Related components:** COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

`GuessRules.MaxAttemptsPerCell` (`XGArcade.Core.Scoring`) is a single
`const int = 2`, read directly by `GuessSubmissionService`,
`LiveRoundContributionService`, and `RoundEndpoints` to decide when a cell
locks against further guessing (REQ-210). This is correct for xG Grid,
where every cell has the same fixed guess allowance. xG Path's puzzle
shape breaks that assumption directly: its "attempt cap" is the number of
clues available for a puzzle (see REQ-1201-REQ-1206) — a value specific
to xG Path, not shared with xG Grid's `2`. A single `const int` on
`GuessRules` cannot represent a second game's own value without an `if`
per call site.

(REQ-1203 originally defined this as `min(club stint count, 5) + 4`,
varying target-player to target-player; a 2026-07-27 revision to REQ-1203
made it a fixed `7` for every xG Path puzzle instead — see
`docs/CHANGELOG.md`. That revision doesn't change this ADR's decision: the
cap is still resolved per-cell through `IGameModule` rather than a shared
constant, since a future game module may still need a genuinely variable
value, and xG Path's own value is still not xG Grid's `2`.)

This is the same structural fork ADR-0040 identifies for the scoring
formula, applied one layer earlier in the guess-submission flow: xG Grid's
"2 guesses per cell" was never really a platform rule, it was xG Grid's
rule, hidden behind a name (`MaxAttemptsPerCell`) that sounds generic.

## Decision

The attempt cap becomes a per-cell value the owning game module reports,
not a shared constant. `IGameModule` gains a method to resolve a given
cell's own max-attempts value (queried the same way `GetCellIdsAsync`
already is, via `IGameModuleResolver`, keyed by the round's `GameKey` —
no new resolution mechanism, this extends the existing interface).
`GuessSubmissionService`, `LiveRoundContributionService`, and
`RoundEndpoints` all replace their direct reads of
`GuessRules.MaxAttemptsPerCell` with a call through this method.

xG Grid's implementation returns the constant `2` for every cell,
unconditionally — REQ-210's existing behavior and every existing test
describing it are unchanged; this is a pure extraction, not a rule change,
identical in spirit to how ADR-0040 extracts xG Grid's scoring formula
without altering it. xG Path's implementation returns a fixed `7` for
every puzzle (REQ-1203), computed at instance-generation time and
stored alongside that puzzle's own state (not recomputed from scratch on
every guess-submission call, to keep a puzzle's cap stable even if
underlying reference data changes later).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add a `MaxAttempts` column directly on the shared `Guess` or `Round` table | No new interface method | `Guess`/`Round` are Core-owned tables (ADR-0003); populating a game-specific number onto them from outside `IGameModule` reintroduces exactly the coupling ADR-0003 eliminated for instance references | Contradicts the existing generic-reference pattern this codebase already committed to |
| Keep one global constant, special-case xG Path with an `if` in each of the three call sites | Smallest diff | Same anti-pattern ADR-0040 rejects for scoring — three call sites would each need their own branch, and a third game doubles that again | Doesn't scale, duplicates logic across call sites instead of centralizing it behind `IGameModule` |
| Per-cell value resolved through `IGameModule` (chosen) | Single new method, mirrors `GetCellIdsAsync`'s existing shape exactly; xG Grid's behavior is provably unchanged (constant-2 in, constant-2 out); a puzzle's cap is decided once at generation time, not recomputed inconsistently across call sites | One more `IGameModule` method every future game module must implement | Best fit: extends an interface this codebase already uses for exactly this kind of per-cell, per-game fact |

## Consequences

- Positive: a cell's attempt cap is defined in exactly one place (the
  owning game module), read identically by every caller that needs it;
  xG Grid needs no behavior change to adopt this, only a mechanical
  extraction
- Negative / trade-offs accepted: `IGameModule` grows a fourth method,
  meaning any future third game module must also implement it (even if,
  like xG Grid, it just returns a fixed number) — accepted as a small,
  uniform cost rather than a special case for games with a variable cap
- Follow-up: `GuessRules.MaxAttemptsPerCell` the constant should be deleted
  outright once xG Grid's own call sites are migrated to read from
  `IGameModule` instead — it must not survive as unused dead code, and it
  must not survive as a "default" silently used by any code path (a game
  module that forgets to implement this method should fail loudly, not
  fall back to xG Grid's 2)

## For AI agents

Do not read or reintroduce `GuessRules.MaxAttemptsPerCell` as a literal `2`
anywhere in `Core.Scoring` once this is implemented — every attempt-cap
check must go through `IGameModule`. If you find a call site still
comparing against a hardcoded `2`, that is exactly the bug this ADR exists
to prevent; fix the call site to resolve the cap from the game module
instead of copying the old constant's value into new code.
