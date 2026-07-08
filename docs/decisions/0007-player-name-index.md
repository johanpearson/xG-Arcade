# ADR-0007: A broad player name index for autocomplete, separate from the narrow validated attribute cache

- **Status:** Accepted
- **Date:** 2026-07-05
- **Related requirements:** REQ-207, REQ-208, REQ-209
- **Related components:** COMP-05 (Games.Grid), COMP-06 (Data.PlayerStore), COMP-10 (new)

## Context

ADR-0001 established that `PlayerAttribute` (the data used to validate
whether a guess is correct) grows incrementally — it only ever contains
combinations an actually-generated grid has needed. This is good for cost
and correctness, but it creates a problem if autocomplete suggestions are
sourced from that same narrow cache: early on, and really at any point, the
set of "names the app will suggest as you type" is a small, curated pool
that's disproportionately made up of valid answers. A player can often find
the correct answer just by seeing what autocompletes, which defeats the
point of the puzzle.

## Decision

Introduce a second, deliberately broad data source used **only** for
autocomplete, entirely separate from `PlayerAttribute`:

- **`PlayerNameIndex`** (COMP-10, `XGArcade.Data`): a name-and-id index
  covering many thousands of professional footballers, built from a single
  bulk import (e.g. a Wikidata SPARQL query for humans with the occupation
  "association football player," or a comparable dataset), refreshed
  periodically rather than incrementally. It stores just enough to search
  and disambiguate — name, known aliases, birth year, primary
  nationality/club for display — not full attribute validation data.
- Autocomplete (REQ-207) queries `PlayerNameIndex` only. Correctness
  checking (REQ-203) continues to query `PlayerAttribute` (plus live-lookup
  fallback, per ADR-0001) exactly as before. A name being suggested implies
  nothing about whether it's valid for the current cell.
- This is a deliberate, narrow exception to ADR-0001's "no bulk upfront
  import" principle, justified because: (a) it's a much smaller, simpler
  dataset than full attribute validation data, (b) it exists specifically
  to prevent an information leak, not to serve correctness-checking, and
  (c) it's refreshed infrequently as a whole, not built combination-by-combination.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Source autocomplete from `PlayerAttribute` (status quo before this ADR) | No new data source needed | Directly leaks answer validity — the exact problem this ADR exists to fix | Fails the game's core fairness requirement |
| No autocomplete at all, free-text only | Simplest, no leak possible | Bad UX (typos, no help with spelling), and REQ-208's name-matching still needs *some* index of known names/aliases to match against | Autocomplete is valuable UX; the fix is what it's sourced from, not removing it |
| Autocomplete queries an external API live, no local index | No bulk import needed | Latency on every keystroke; still needs the alias/fuzzy-matching logic somewhere, so a local index ends up necessary anyway for REQ-208 | A local index serves both autocomplete and matching; a live call per keystroke is worse UX for no real benefit |

## Consequences

- Positive: guessing is no longer trivially solvable by watching
  autocomplete; `PlayerAttribute`'s incremental-cache benefits (ADR-0001)
  are preserved unchanged
- Negative / trade-offs accepted: one more data source to keep fresh; a
  name in `PlayerNameIndex` with no corresponding `PlayerAttribute` entry
  is a normal, expected state (most suggested names won't be valid for any
  given cell), not a bug
- Follow-up: monitor whether the name-index refresh cadence needs to be
  more frequent than initially assumed (e.g. after transfer windows) —
  start with a manual/quarterly refresh and tighten if names are noticeably missing

## For AI agents

Never wire autocomplete to query `PlayerAttribute` or `PlayerOverride`
directly — it must go through `PlayerNameIndex` (COMP-10) only. Never treat
"appears in `PlayerNameIndex`" as sufficient for correctness — that
determination is exclusively `PlayerAttribute`'s (plus override/live-lookup)
job, per ADR-0001. If a task seems to require merging these two data paths
for convenience, stop and flag it — that would silently reintroduce the leak.
