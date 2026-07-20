# ADR-0032: Wikidata guess-time fallback data is now auto-verified too (supersedes ADR-0029)

- **Status:** Accepted
- **Date:** 2026-07-20
- **Related requirements:** REQ-211, REQ-502, REQ-503
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-05 (Games.XGGrid)
- **Supersedes:** ADR-0029 (its fallback-specific carve-out is reversed; ADR-0029 remains
  in `docs/decisions/` marked `Status: Superseded by ADR-0032`, not deleted)

## Context

ADR-0029 (2026-07-19) deliberately split `Confidence` at write time by *why* a Wikidata
lookup ran, not just that it came from Wikidata: `WikidataLookupOrigin.Sync` (routine
grid-generation cache-miss or cache-warming — the vetted per-category intersection query)
persisted `Confidence = "verified"`; `WikidataLookupOrigin.GuessTimeFallback` (REQ-211's
guess-time re-check against one specific player, triggered by one player's guess against
one already-generated cell) stayed `"unverified"`, specifically so an admin could still
spot-check this narrower, less-vetted path.

One day later, the product owner has decided all Wikidata-sourced data should be verified
by default, including the guess-time fallback path — REQ-211's own 2026-07-20 status note
records this. This is a real reversal of ADR-0029's central distinction, not an extension
of it: ADR-0029's whole rationale for keeping the fallback path reviewable ("worth a human
being able to spot-check it") no longer holds as a design constraint. The decision changes
what data other players' cell correctness immediately depends on (a live-fetched fallback
result becomes the stable cached answer for the rest of the round the moment it's written,
per `architecture-document.md` §8's "Consistency of correctness" row) with no human review
step in between, so it is recorded here rather than made silently.

## Decision

`WikidataLookupService.ConfidenceFor` now returns `"verified"` for both
`WikidataLookupOrigin` values:

```csharp
private static string ConfidenceFor(WikidataLookupOrigin origin) => origin switch
{
    WikidataLookupOrigin.Sync => VerifiedConfidence,
    WikidataLookupOrigin.GuessTimeFallback => VerifiedConfidence,
    _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
};
```

The `WikidataLookupOrigin` enum and its two call sites (`GetMatchCountAsync` → `Sync`,
`RefreshCellFromLiveLookupAsync` → `GuessTimeFallback`) are kept, not collapsed away —
the distinction is still meaningful for logging/debugging/future re-differentiation, it
just no longer drives a different `Confidence` value. `PersistAttributeAsync`'s call
into `AddPlayerDataAsync` is otherwise unchanged.

A second one-time bulk-flip is needed for the same reason ADR-0029 needed one: any
`PlayerData` rows written via `GuessTimeFallback` between ADR-0029 shipping (2026-07-19)
and this change landing are still `unverified` in the database, and no row records which
origin wrote it, so they can't be selectively identified after the fact. Re-run
`verify-wikidata-player-data` (the existing CLI verb from ADR-0029, safe to re-run —
idempotent, "a second run finds nothing left to update") to flip that remaining window's
rows, so REQ-503's "review queue is empty by construction going forward" claim is
actually true rather than merely "stops growing here."

REQ-502/503's review queue itself is not removed — same as ADR-0029, it's earmarked for
a future player-suggestion/correction channel, still unbuilt.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Auto-verify guess-time fallback too (chosen) | Matches the product owner's stated default; one `Confidence` policy, not two; removes the last source of new `unverified` rows | No human review of the narrowest, least-vetted lookup path anymore; a single bad/ambiguous live Wikidata response is now immediately treated as ground truth for every subsequent guess on that cell | This is what was explicitly requested, 2026-07-20 |
| Keep ADR-0029's split (status quo) | Preserves a human check on the narrowest, riskiest data path | Contradicts the explicit 2026-07-20 product decision | Rejected — not what was asked for |
| Add per-fallback-write review without blocking correctness (e.g. write verified immediately but also queue a review item) | Gets both immediacy and eventual human oversight | New mechanism (a queue write that doesn't gate `Confidence`) not requested, adds scope beyond what was asked | Rejected as scope creep — not what REQ-211's status note describes |

## Consequences

- Positive: no code path persists `Confidence = "unverified"` anymore (until a real
  player-suggestion channel exists) — REQ-503's admin review list becomes genuinely
  empty rather than a slow-growing queue, once the backfill above runs.
- Negative / trade-offs accepted: the one guess-time-fallback-specific safeguard
  ADR-0029 introduced (a human can catch a bad live lookup before it becomes the
  accepted answer for a cell) is gone. A wrong/ambiguous Wikidata response on this path
  is now indistinguishable, in `Confidence`, from a routine vetted sync — an admin
  browsing "verified" data has no way to know which rows came from the narrower path
  without additional instrumentation (not built here).
- Negative / trade-offs accepted: a second one-time bulk-flip CLI run is required for
  the same reason ADR-0029 needed its first one — this is the second time in two days
  this exact remediation shape has been needed; if `Confidence` policy changes again,
  expect a third.
- Follow-up: if a real player-suggestion/correction channel (REQ-502/503's originally
  intended third source) is ever built, it becomes the review queue's sole source, same
  as ADR-0029 already anticipated — unchanged by this decision.

## For AI agents

If code you are about to write would contradict this decision, stop and flag it rather
than silently working around it — either the decision needs a new ADR that supersedes
this one, or the approach needs to change. Do not reintroduce a per-origin `Confidence`
split without a new ADR; `WikidataLookupOrigin` still exists and is still passed
explicitly by every caller, but as of this ADR it no longer determines `Confidence`.
