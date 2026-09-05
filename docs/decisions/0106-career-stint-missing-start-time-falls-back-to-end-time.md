# ADR-0106: A career stint missing its Wikidata start time falls back to its end time

- **Status:** Accepted
- **Date:** 2026-09-04
- **Related requirements:** REQ-1203, REQ-1404, REQ-1406
- **Related components:** COMP-07 (DataSync), COMP-17 (Games.XGConnect)

## Context

`SparqlResponseParsers.ParseCareerStintBindings` (`XGArcade.DataSync`) turns
each Wikidata P54 ("member of sports team") statement row into a
`WikidataCareerStintEntry`. Since `PlayerCareerStint.StartYear` is
non-nullable, the parser has always required a usable P580 ("start time")
qualifier to construct a row at all — a row whose P54 statement never got a
start-time qualifier filled in on Wikidata was silently dropped, even
though the query fetches `?startTime` as `OPTIONAL` specifically so the
rest of a player's career still comes back when one statement lacks it.

ADR-0105 (same day) fixed a real, reported xG Connect bug — Reece James'
2019 loan spell at Wigan Athletic was never being discovered — by making
`PlayerCareerOverlapService` always refresh both players' full career from
Wikidata, rather than trusting a narrow, previously-cached row set as
complete. That fix shipped, and **the exact same reported guess (Jonas
Olsson connecting to Reece James via their shared 2019 Wigan Athletic loan)
still failed** on the very next attempt, with no cached-data staleness
possible (each chain-step submission is a fresh live check — see
`ConnectChainStepService.SubmitStepAsync`, no result is ever cached across
attempts).

That ruled out staleness and pointed at the fetch/parse path itself. Jonas
Olsson's Wigan Athletic loan is a short, lower-profile stint late in his
career — exactly the kind of P54 statement most likely to have a
contributor-filled `on loan from` (P1210) and appearance count (P1350) but
no precise start-time (P580) qualifier on Wikidata. `ParseCareerStintBindings`
was dropping that row outright: fetched from Wikidata (the query itself
never filtered it out — `?startTime` is `OPTIONAL`), then discarded by the
parser for lack of a start year, every single refresh, regardless of how
often or how unconditionally the refresh runs. ADR-0105's own Follow-ups
section had already named this exact gap without fixing it: "a full refresh
can still miss a real stint if Wikidata's own record of it has no usable
date."

## Decision

When a P54 statement's `startTime` binding is missing or unparseable but
its `endTime` binding IS usable, `ParseCareerStintBindings` now falls back
to using that end year as the `StartYear` too — i.e. it assumes a
single-year stint rather than dropping the row. A row with neither a usable
`startTime` nor a usable `endTime` still carries no year to anchor on and
is still skipped, exactly as before this fix.

This codebase already only needs year-level granularity for every
career-stint use (`TryParseXsdDateTimeYear` discards month/day precision
everywhere it's called) — a single-year approximation is not a loss of
precision this system was using anyway, and "the club and the year are
both real, single-season loans commonly have exactly this Wikidata data
shape (no start date, an end date, and/or an appearance count)" is a
correct default assumption for the missing-start-time case specifically,
not a guess invented for this fix.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Fall back start year to end year when start is missing (chosen) | Directly recovers the reported incident's exact data shape; no schema change; reuses the existing year-only granularity this codebase already accepts everywhere | A stint that actually spans two calendar years (e.g. a loan from August 2018 to May 2019) with no start-time qualifier would be recorded as starting in 2019, one year later than reality | The narrower, more common case for a *missing start time specifically* is a short, single-season loan — and this system's overlap/matching logic only compares years, so a stint that's wrong by one year at the edges is still far better than a stint that's silently absent entirely |
| Query a different Wikidata property for a season/point-in-time qualifier (e.g. P1642, or a "sports season" qualifier) as a richer fallback | Could recover a more precise year in more cases | New SPARQL shape, new parsing branch, no evidence yet that Wikidata reliably carries this for the missing-start-time statements this bug actually hits | Solving a problem not yet shown to exist beyond the one reported case; revisit if a future report shows this fallback still isn't enough |
| Leave the row dropped, treat this as an acceptable data-source gap | No code change | Directly contradicts a live, reported, twice-confirmed (ADR-0105 didn't fix it) correctness bug | Not acceptable — this was reported by the product owner as still broken after the previous fix shipped |

## Consequences

- Positive: closes the Reece James / Jonas Olsson incident for real —
  ADR-0105 made the refresh unconditional, and this ADR makes the refresh's
  own parsing actually keep the row that refresh was supposed to recover.
- Positive: applies uniformly everywhere `ParseCareerStintBindings` is used
  (`PlayerCareerStintRefreshService`, feeding both xG Path's ADR-0054 fetch
  and xG Connect's ADR-0105 fetch) — not a narrow xG-Connect-only patch, so
  the identical xG Path duplicate-node-shaped gap (a target's real loan
  spell silently missing from their own puzzle) is closed too, for free.
- Negative / trade-off accepted: a stint whose true start year is one
  calendar year earlier than its end year, and whose Wikidata statement has
  no start-time qualifier at all, is now recorded one year later than
  reality (see Alternatives). Not measured against real Wikidata data at
  scale — accepted because the alternative (silently missing the stint
  entirely) is strictly worse for this codebase's purposes.
- Follow-up: if a future report shows this one-year approximation itself
  produces a wrong answer (rather than a missing one), reconsider the
  richer season-qualifier fallback from the Alternatives table above — with
  a real example in hand, not a hypothetical one.

## For AI agents

`ParseCareerStintBindings`' start-year resolution is `startTime`, falling
back to `endTime` — not a single unconditional read of `startTime`. Do not
"simplify" this back to requiring `startTime` alone without re-reading this
ADR and ADR-0105 first: that combination (unconditional refresh + a
parser that still drops the exact statement shape a real incident hit) is
what caused the same reported bug to survive one fix already.
