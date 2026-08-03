# ADR-0057: Guess-time live lookup for wrong-but-real guess photo display (REQ-216) is Wikidata-only, fails silently

- **Status:** Accepted
- **Date:** 2026-08-03
- **Related requirements:** REQ-216 (new), REQ-211 (existing, not modified), REQ-214 (existing, UI template)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-10 (PlayerNameIndex)

## Context

REQ-216 (drafted this session, direct product-owner sign-off) shows the
guessed player's photo and canonical name, with a red border, when a cell
locks with its final guess still incorrect — but only when that guess
string matched a real player in `PlayerNameIndex` (ADR-0007) who simply
isn't the cell's correct answer. `PlayerNameIndex` carries no photo of its
own (its `PhotoUrl` column was removed by the `RemovePlayerNameIndexPhotoUrl`
migration, 2026-07-18, once autocomplete turned out never to use it), and
REQ-214's existing correct-guess photo is sourced from `Player.PhotoUrl`,
populated only because the cell's own correctness query (REQ-101/102)
already resolved that specific player as the answer — a wrong guess has no
equivalent resolved record by construction.

REQ-211/ADR-0011 already define a guess-time live-lookup mechanism
(Wikidata-first, API-Football fallback, `ExternalApiUsage` threshold gate
on the fallback path, fail-closed-as-incorrect on exhaustion, immediate
same-request persistence, and a narrow trigger condition — only when a
guess matches a real `PlayerNameIndex` candidate with no existing
`PlayerAttribute` data). That mechanism exists to answer a correctness-
critical question: "is this guess right?" REQ-216's case is different in
kind — the guess is already known to be wrong; a lookup here would exist
purely to fetch cosmetic display data, never to determine a scoring
outcome. ADR-0011's budget model and fail-closed semantics were never
built with a non-correctness caller in mind, so simply pointing REQ-216 at
REQ-211's trigger unmodified would silently stretch what "narrow" (per the
CLAUDE.md guess-time-lookup rule) was scoped to allow, and would let a
cosmetic feature spend the same scarce, shared API-Football budget that
correctness-critical lookups depend on.

## Decision

REQ-216's photo resolution reuses ADR-0011's `WikidataClient` and its
Wikidata-first ordering, but as its own distinct, lower-priority trigger,
separate from REQ-211's:

- **Wikidata only — no API-Football fallback.** Cosmetic display value
  does not justify spending the shared, scarce `ExternalApiUsage` budget
  that correctness-critical REQ-211 lookups rely on.
- **Fires once, at cell-lock time only** — never per incorrect attempt,
  and never for a guess that matched no real `PlayerNameIndex` candidate
  at all (that case shows no name/photo, unchanged, per REQ-216).
- **Fails silently on timeout or no-match**: render no photo, following
  REQ-214's existing graceful-fallback path (no broken-image icon, no
  error state, no layout change). This trigger never fails closed as
  "incorrect" — there is no correctness verdict left to compute for a
  guess already known to be wrong, so ADR-0011/REQ-211's fail-closed
  semantics do not apply here at all.
- Persisted immediately in the same request if resolved, same as
  REQ-211's existing persistence discipline — never batched.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Extend REQ-211's trigger unmodified (Wikidata + API-Football fallback, fail-closed) | Single code path, no new trigger concept | Spends the correctness-critical API-Football budget on a purely cosmetic feature; "fail closed as incorrect" has no meaning for a guess already known to be wrong | Conflates a cosmetic caller with a correctness-critical one; exactly the scope creep the CLAUDE.md "narrow and never deferred" rule exists to prevent |
| No new lookup at all — only show a photo when the wrong-but-real player's data is already incidentally cached from resolving some other cell | Zero new external calls, simplest to build | Makes the product owner's confirmed ask ("show the photo when resolvable") unreliable by construction — photo would appear rarely and inconsistently, contradicting REQ-216's explicit acceptance criteria | Rejected: doesn't actually deliver what was asked for |
| Re-add `PlayerNameIndex.PhotoUrl` | Would let a photo be sourced without any new lookup | Re-opens a settled call (that column was deliberately removed, 2026-07-18, once autocomplete proved not to need it) for a different consumer than the one that removed it — no analysis was done on whether a name-matching table is the right place for photo data used only for a rare, wrong-guess display case | Re-litigates a prior decision without new information justifying it; a scoped, budget-aware lookup solves the problem without touching ADR-0007's table |

## Consequences

- Positive: correctness-critical REQ-211 lookups keep their full
  `ExternalApiUsage` budget headroom untouched by this feature; REQ-216's
  photo remains reliably resolvable whenever the guess matched a real
  player, rather than only incidentally
- Negative / trade-offs accepted: a wrong-but-real guess whose player
  happens to have poor Wikidata coverage (or where Wikidata times out)
  will show no photo, with no fallback source to try — accepted, since
  this is a display enhancement, not a scoring outcome
- Follow-up: if usage data later shows a meaningful fraction of
  wrong-but-real guesses failing to resolve a photo purely due to
  Wikidata timeouts (not genuine data absence), revisit whether a fallback
  is worth adding — but only as a deliberate, budget-aware decision, not
  by quietly reusing REQ-211's fallback tier
- Addendum (2026-08-03): this decision only scopes REQ-216's lookup out of
  the shared `ExternalApiUsage`/API-Football budget — it does not add any
  rate-limiting of its own against WDQS (Wikidata's public query service)
  itself. That is not a new gap this ADR introduces: REQ-211's own existing
  guess-time correctness fallback (`GridGameModule.RefreshCellFromLiveLookupAsync`)
  is equally uncapped against WDQS today, so this trigger is at least no
  worse than existing precedent. It is worth naming explicitly, though,
  since REQ-216's trigger plausibly fires at meaningfully higher volume —
  once per distinct incorrect final guesser per cell — than REQ-211's own
  narrower missing-`PlayerAttribute`-data trigger. If WDQS-level
  rate-limiting is ever added, it should cover both callers together, not
  be bolted onto this one alone.

## For AI agents

If you are implementing REQ-216's photo resolution, do not call this new
trigger through `IExternalApiUsage`'s `GuessTimeLookupThreshold` gate or
any API-Football client — that gate and that fallback tier belong to
REQ-211's correctness-critical path only. If you find yourself reaching
for API-Football "just as a fallback, same as REQ-211," stop — that's the
exact scope creep this ADR exists to prevent. If a future requirement
needs a genuinely different lookup budget model for this trigger, that
needs its own ADR superseding this one, not a silent code change.
