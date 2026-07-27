# ADR-0046: A timeout during REQ-211's guess-time live lookup is a distinct, non-scoring exception signal, not a swallowed empty result

- **Status:** Accepted
- **Date:** 2026-07-27
- **Related requirements:** REQ-210, REQ-211
- **Related components:** COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid), COMP-07 (DataSync.Clients)

## Context

REQ-211's guess-time live-lookup fallback (`GridGameModule
.RefreshCellFromLiveLookupAsync`, via `WikidataLookupService`/
`WikidataClient`) re-runs a cell's own Wikidata intersection query when
cached data doesn't already answer a guess (ADR-0010/ADR-0018). Every
`IWikidataClient` intersection-query method has always swallowed a timeout
(15s, `WikidataClient.RunIntersectionQueryAsync`) to an empty result — a
deliberate choice for REQ-103's grid-generation use of the same client
(ADR-0011: "never block grid generation on a Wikidata failure").

That same swallow-to-`[]` behavior is wrong for REQ-211's guess-time call
site specifically: an empty result there is indistinguishable from "Wikidata
answered and found no match," so `GridGameModule.ScoreSubmissionAsync` falls
through to its existing cache-only "incorrect" path and
`GuessSubmissionService` persists a scored, attempt-consuming `Guess` row —
even when the guess might well be correct and Wikidata simply didn't
respond in time. This is exactly the bug reported: guessing "Clarence
Seedorf" for Ajax × AC Milan returned a fetch failure once, and the retry
was scored incorrect, burning both of REQ-210's two attempts on a guess
that was never actually evaluated.

`Core` (where `GuessSubmissionService`/COMP-04 lives) must never reference
`XGArcade.DataSync`-specific types (ADR-0003's boundary, generalized beyond
its original Round/game-instance scope to "Core depends on nothing
game/infra-specific"), so whatever signal crosses from `GridGameModule`
(COMP-05) back into `GuessSubmissionService` (COMP-04) needs a type Core
already owns.

## Decision

Introduce a narrow, opt-in `throwOnTimeout` parameter (default `false`) on
each of `IWikidataClient`'s five intersection-query methods. `WikidataClient`
still swallows a timeout to `[]` by default — REQ-103's grid-generation call
path is completely unchanged. `WikidataLookupService` sets `throwOnTimeout =
true` only when `origin == WikidataLookupOrigin.GuessTimeFallback`; on a
timeout in that case, `WikidataClient` throws `WikidataQueryException`
instead.

`GridGameModule.RefreshCellFromLiveLookupAsync` is the one place a
`DataSync`-specific exception is allowed to cross into a `Core`-facing
contract: it catches `WikidataQueryException` and translates it into a new
`XGArcade.Core.Games.LiveLookupUnavailableException`, defined in `Core`
itself (not in `Games.XGGrid`, and not in `DataSync`) so both halves of the
contract — "an `IGameModule.ScoreSubmissionAsync` implementation may throw
this" and "`GuessSubmissionService` catches this" — depend only on a type
`Core` already owns. `GuessSubmissionService.SubmitGuessAsync` catches it
and returns a new `GuessSubmissionOutcome.LiveLookupUnavailable` — before
ever touching `guessRepository`, the same "return before persisting" shape
REQ-209's disambiguation branch already uses, so no `Guess` row is written
and no attempt is consumed. `GuessEndpoints.cs` maps this outcome to HTTP
503, with a message telling the client to retry.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Result-type/nullable signal (e.g. `ScoreResult` gains a third `Timeout`/`Unknown` state, or `RefreshCellFromLiveLookupAsync` returns `bool?` instead of `bool`) instead of an exception | No exception-based control flow; every caller sees the possibility in the method signature itself | `ScoreResult` is returned by every `IGameModule.ScoreSubmissionAsync` implementation and consumed by scoring/uniqueness code that has no reason to know about a Wikidata-specific failure mode — widening it (or `RefreshCellFromLiveLookupAsync`'s private return type) to carry this one game module's one dependency's one failure mode leaks an implementation detail into a much broader contract. A `bool?` on the private helper would still need to propagate the same "not scored" meaning up through `ScoreSubmissionAsync`'s existing `ScoreResult`, hitting the same widening problem one level up | An exception is a genuinely rare, exceptional condition (Wikidata not responding in 15s) — not a routine branch every caller of `ScoreResult` needs to handle on every call, which is what a widened result type would demand |
| Increase `WikidataClient`'s intersection-query timeout instead of distinguishing timeout from no-match | Simplest possible change — no new type, no new outcome, no new HTTP status | Doesn't fix the actual bug: it only makes the false-negative window rarer, not eliminates it, and a slower timeout makes every guess-time fallback call (the "guessing is slow" half of this bug report) worse, not better, for every guess that genuinely has no match. Trades one symptom for a worse version of the other | The report was about two symptoms at once (slow guessing AND wrong scoring on failure) — a longer timeout would make the first worse while only partially helping the second |
| Accept the false-negative rate as a known, documented tradeoff (do nothing beyond a docs note) | Zero code change | The reported case (Seedorf, a real, unambiguous Ballon d'Or-tier player) is not a rare edge case at the margins of correctness — it's a routine intersection query hitting an ordinary network hiccup, directly contradicting REQ-211's own stated purpose ("never wrongly told I'm wrong"). Also burns a real REQ-210 attempt for a guess that was never actually evaluated, on a per-cell budget only sized for genuine wrong guesses | The whole point of REQ-211 is to prevent exactly this outcome; leaving it as a known gap defeats the requirement it's supposed to satisfy |

## Consequences

- Positive: a Wikidata timeout during the guess-time fallback no longer
  consumes a REQ-210 attempt or scores a guess that was never actually
  checked; the player gets a clear, retryable 503 instead of a silent wrong
  "incorrect."
- Positive: REQ-103's grid-generation call path is provably unaffected —
  `throwOnTimeout` defaults to `false` and only `WikidataLookupOrigin
  .GuessTimeFallback` ever sets it `true`.
- Negative / trade-offs accepted: `GuessSubmissionOutcome` now has a branch
  (`LiveLookupUnavailable`) where none of `IsCorrect`/`AttemptCount`/`Locked`
  is meaningful, same as the existing disambiguation branch — every switch
  over `GuessSubmissionOutcome` (e.g. `GuessEndpoints.cs`) must keep handling
  it explicitly rather than falling through to a generic 500, or a future
  unhandled-case bug would silently regress this fix.
- Negative / trade-offs accepted: this is the first place `Core.Games`
  defines an exception type specifically for a game module to throw back
  into `Core` — a new, narrow carve-out in the `IGameModule` boundary
  (ADR-0003) that future game modules integrating a live external
  dependency of their own should follow, not bypass with their own
  ad-hoc exception type.
- Follow-up: if a second game module ever needs the same "infra dependency
  timed out mid-guess" signal, confirm `LiveLookupUnavailableException` is
  generic enough to reuse as-is before introducing a second, similarly-named
  type in `Core.Games`.

## Status note (2026-07-27, follow-up)

Real usage after merge (#123) surfaced a gap this ADR's own evidence had
already predicted: guessing "Clarence Seedorf" for Ajax × AC Milan
consistently returned the `LiveLookupUnavailable` 503 this ADR introduces,
never resolving — a genuinely correct guess that Wikidata could answer, but
not within 15s. `BuildClubClubIntersectionQuery`'s two full `P54`
statement-path joins (needed to preserve "ever played for," not "currently
plays for" — see that method's own comment) are exactly the query shape
ADR-0011's evidence names as capable of taking up to 27s under WDQS load.

This does **not** reverse the "Increase `WikidataClient`'s intersection-query
timeout" alternative rejected in the table above — that alternative was
about widening the timeout *instead of* adding the exception-based
distinction, which really would have traded one bug for a worse one (a
slower failure mode on every guess, at a time when the live fallback still
fired on every unresolved guess, not just ones worth a live call). REQ-211's
`PlayerNameIndex` gate (landed in the same PR as this ADR) already
eliminated that cost for ordinary wrong guesses — the live fallback now
only ever runs for a guess that matched a real, indexed player. Given that,
widening *only* the guess-time-fallback budget — leaving REQ-103/grid
generation's 15s completely untouched — has none of the downside the
rejected alternative had, and directly fixes the "correct guess can never
be verified" gap this status note describes.

`WikidataClient` gained a second constructor parameter,
`guessTimeFallbackQueryTimeout` (default 28s — comfortably covering
ADR-0011's documented 27s worst case with a small margin), selected instead
of the original `queryTimeout` (still 15s, still REQ-103's only budget)
whenever `throwOnTimeout` is `true` — i.e. only for
`WikidataLookupOrigin.GuessTimeFallback`. Verified this doesn't reintroduce
the "failed to fetch" network-level failure this ADR's own Context section
describes: no Azure Container Apps ingress timeout is configured in
`infra/bicep/modules/backend-container-app.bicep`, and the frontend's guess
submission has no client-side `AbortSignal`/timeout of its own, so a 28s
round trip completes as an ordinary (if slow) HTTP response, not a dropped
connection.

## For AI agents

`LiveLookupUnavailableException` (`XGArcade.Core.Games`) exists specifically
so a game module's own live-lookup dependency can signal "genuinely
unknown," never "incorrect," across the `IGameModule`/`GuessSubmissionService`
boundary — Core itself must never catch or reference the underlying
game/DataSync-specific exception (e.g. `WikidataQueryException`) directly;
only the throwing game module may do that translation. Don't widen
`throwOnTimeout`'s default to `true` for any existing `IWikidataClient`
caller without re-confirming REQ-103/ADR-0011's "never block grid generation
on a Wikidata failure" guarantee still holds. Don't add a new
`GuessSubmissionOutcome` branch that skips the "return before touching
guessRepository" shape this one and REQ-209's disambiguation branch both
use — an attempt must never be consumed for an outcome that isn't a real,
evaluated guess. `WikidataClient` has two separate timeout fields —
`queryTimeout`/`_queryTimeout` (15s, REQ-103/grid generation, always used
when `throwOnTimeout` is `false`) and `guessTimeFallbackQueryTimeout`/
`_guessTimeFallbackQueryTimeout` (28s, used only when `throwOnTimeout` is
`true`) — don't collapse these back into one field, and when writing a test
that exercises `throwOnTimeout: true`'s timeout branch, set
`guessTimeFallbackQueryTimeout` (not just `queryTimeout`) to a short value,
or the test will wait out the real 28s default.
