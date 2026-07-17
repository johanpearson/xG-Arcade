# ADR-0027: Configuration-bound RoundDuration + daily safety-poll cron

- **Status:** Accepted
- **Date:** 2026-07-17
- **Related requirements:** REQ-301
- **Related components:** COMP-03 (Core.Rounds)

## Context

REQ-301's acceptance criteria call for round frequency to be "configured...
so play frequency can be adjusted without a code change." Tier 0 never
built that part: `RoundSchedulingOptions.RoundDuration` was a hardcoded
`TimeSpan.FromDays(4)` in `Program.cs`, deliberately coupled to
`generate-round.yml`'s Tue+Fri cron cadence — `RoundDuration` had to stay
`>=` the longest gap between two consecutive cron firings (Fri->Tue, 4
days), or a round could fully close before the next scheduled run ever
generated its successor, reproducing REQ-301's own "dead app" failure mode
via misconfiguration instead of a real failure (see `NOTES.md`'s
2026-07-10 entry for the worked-through derivation of why an "average" or
"roughly matching" duration isn't sufficient — only the longest gap
matters).

The immediate ask: make `RoundDuration` adjustable without redeploying
code, and set it to 48 hours now. Naively, the obvious pairing is a cron
that fires exactly every 2 days (`*/2` on cron's day-of-month field) to
match a 48h `RoundDuration` exactly. That pairing has a real problem:
cron's day-of-month field resets every calendar month, so `*/2` doesn't
actually produce constant 48h gaps — it produces an irregular ~24h gap at
some month boundaries and an *exact* 48h gap at others (worked through by
hand: for an N-day month, the last selected day is `N` if `N` is odd or
`N-1` if `N` is even, so the gap to day 1 of the next month is 1 or 2 days
respectively). A 48h `RoundDuration` against an exact 48h max cron gap has
zero safety margin — if GitHub Actions' scheduler fires even slightly late
(a documented, known behavior of GH Actions scheduled workflows, not a
hypothetical), the round can already be closed by the time the job runs,
which is exactly the failure class this coupling exists to prevent. It
also means the max-gap arithmetic has to be re-verified by hand every time
either value changes — the same brittleness the Tue/Fri cadence already
had, just relocated.

## Decision

Two changes, made together:

1. **`RoundSchedulingOptions.RoundDuration`'s default is now
   configuration-bound**, not hardcoded: `Program.cs` reads
   `RoundScheduling:RoundDurationHours` (`appsettings.json` ships a default
   of `48`; the deployed Container App can override it via the
   `RoundScheduling__RoundDurationHours` env var with no code change or
   rebuild). `POST /internal/generate-round` additionally accepts an
   optional `roundDurationHours` query parameter that overrides the
   configured default for that single generation call only — it never
   mutates the shared `RoundSchedulingOptions` singleton.
   `generate-round.yml` exposes this as an optional `workflow_dispatch`
   input (`round_duration_hours`, no default, so scheduled cron runs never
   set it), giving a one-off override path that needs neither a config
   change nor a redeploy.

2. **`generate-round.yml`'s cron moves from `0 6 * * 2,5` (Tue+Fri) to
   `0 6 * * *` (daily)**, and is deliberately *not* re-coupled to
   `RoundDuration` the way the old cadence was. `RoundGenerationService`'s
   existing idempotency check (skip generation if an upcoming/not-yet-
   started round already exists) makes a daily firing a no-op on every day
   the current round hasn't ended yet — actual round generation still
   happens roughly every `RoundDuration` (chain-driven via each round's
   fixed `EndTime`, not cron-driven), while the cron's own max gap becomes
   a constant 24 hours regardless of calendar month boundaries. Any
   `RoundDuration >= 24h` — including the 48h default — now has a
   comfortable, constant safety margin instead of an exact-equality edge
   case that needs re-verifying by hand every time either value changes.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| `*/2` day-of-month cron paired with a 48h `RoundDuration` | Matches the user's illustrative "every 2 days" suggestion literally; firing cadence directly mirrors round cadence | Cron's day-of-month field resets every calendar month, producing an irregular 1-day gap at some month boundaries and an *exact* 48h gap at others — zero safety margin against a 48h `RoundDuration` if GH Actions fires even slightly late; also requires re-verifying the max-gap arithmetic by hand every time either value changes, the exact brittleness being eliminated | The reliability cost (a real, recurring "dead app" risk at month boundaries) outweighs the appeal of cron cadence exactly mirroring round cadence |
| Keep a biweekly fixed-day cron (e.g. still Tue+Fri, or some other two-fixed-days pattern) with `RoundDuration` set to the longer of the two gaps | Same known-working pattern as before | Doesn't evenly support 48h rounds at all — a 4-day-gap-tolerant duration is not 48h; reproduces the same "must hand-verify the longest gap" coupling this ADR removes | Doesn't meet the actual ask (48h rounds) and keeps the brittle coupling |
| Full admin-facing scheduling UI / DB-backed schedule config (REQ-301's full long-term acceptance criteria: "a cron expression configured in the system") | Fully general, matches REQ-301's complete long-term vision | No admin surface exists anywhere in Tier 0; building one is Tier 1/2 scope with no evidence yet that ad-hoc config/env-var changes are insufficient | `MVP-SCOPE.md` explicitly warns against pulling Tier 1/2 complexity forward just because a REQ describing it already exists |

## Consequences

- Positive: `RoundDuration` can be changed going forward via a config
  value (env var, no redeploy) for a lasting change, or via a
  `workflow_dispatch` input for a one-off override, satisfying REQ-301's
  "without a code change" criterion within Tier 0's no-admin-UI scope.
- Positive: the cron/duration coupling that previously required hand
  re-verification (documented at length in `NOTES.md`) is replaced by a
  structural invariant (`RoundDuration >= 24h`, cron's daily max gap) that
  doesn't need re-checking every time `RoundDuration` changes, as long as
  it stays `>= 24h` — which the 48h default comfortably satisfies.
- Negative / trade-off accepted: the daily cron runs (and no-ops) roughly
  twice as often as strictly necessary for a 48h cadence. Each run is a
  cheap, idempotent, bearer-token-protected no-op when a round is already
  upcoming — judged an acceptable cost for the reliability gained.
- Negative / trade-off accepted: a `workflow_dispatch` override only
  affects the single round generated by that call; it is not persisted, so
  the next scheduled (cron) run reverts to whatever the configured default
  is. An operator wanting a *lasting* change still needs to update
  `RoundScheduling:RoundDurationHours` (or the env var) separately.
- Follow-up: revisit if Tier 1 ever adds a real admin-facing scheduling
  surface — REQ-301's full acceptance criteria (a cron expression
  configured *in the system*) remain the documented long-term target this
  Tier 0 mechanism is a scoped-down step toward, not a replacement for.

## For AI agents

If a future change makes `RoundDuration` configurable below 24 hours (or
changes `generate-round.yml`'s cron away from daily), the "`RoundDuration
>= cron's max gap`" invariant this ADR relies on must be re-derived by
hand the same way `NOTES.md`'s 2026-07-10 entry did for the old cadence —
don't assume the current 24h/48h numbers stay safe under a different
cadence or a much shorter duration without redoing that check.
