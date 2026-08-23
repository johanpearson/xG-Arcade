# ADR-0086: Admin-triggered Player refresh is a narrow, un-reviewed exception to "set once at creation"

- **Status:** Accepted
- **Date:** 2026-08-23
- **Related requirements:** REQ-513, REQ-1207, REQ-501, REQ-503
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

`Player.FullName`/`Position`/`BirthYear`/`PhotoUrl` are set once, at player
creation, directly from whatever a Wikidata SPARQL query returned at that
moment (REQ-1207's scope note; enforced today by
`PlayerRepository.GetOrCreatePlayersByWikidataQidAsync`, which never
touches an existing `Player` row). No automatic path — grid generation,
cache warming, REQ-211's guess-time fallback, or any backfill service —
ever re-syncs these four fields after creation. This is a deliberate
invariant, reinforced by matching doc comments across `Player.cs` and
`PlayerRepository.cs`.

GitHub issue #239 exposed the failure mode this invariant leaves open: a
bad Wikidata snapshot at the exact moment our system first queried a given
player's QID (transient vandalism, or a since-corrected upstream Wikidata
error) becomes *permanent* corruption in our system, with zero correction
path — silently shown to a player as ground truth (a garbled player name
presented as the "correct answer" for a locked xG Path puzzle). There was
no admin tool, no resync job, and no way to fix this short of raw database
surgery.

ADR-0032 already decided that Wikidata-sourced data is trusted by default
at write time — no per-write human review step gates it, for either the
routine sync path or REQ-211's narrower guess-time fallback. REQ-513
introduces the first correction path for this data, and had to decide
whether that correction re-applies ADR-0032's same trust model, or adds a
stronger safeguard given that the corruption it exists to fix flowed from
that exact model.

## Decision

Scope the exception as narrowly as the problem requires, and keep
ADR-0032's trust model rather than reopening it:

- **Admin-triggered only**, one player per call: `POST
  /admin/players/{id}/refresh-from-wikidata`, gated by the existing
  `"Admin"` authorization policy, registered in every environment
  including Production (this is a real production data-correction tool,
  not a test aid — REQ-505/506's non-Production-only precedent does not
  apply here).
- **Re-fetches by the player's own already-stored `WikidataQid`** — the
  admin never supplies a QID, name, or any other value directly. This
  avoids introducing a second, manual way to set these four fields that
  could itself become a new source of error.
- **Per-field diff, never a blanket rewrite**: a differing non-null
  fetched value overwrites the stored one; a null/missing Wikidata binding
  for a field never overwrites (absence is not evidence the stored value
  is wrong — the same principle ADR-0046 already applies to a guess-time
  lookup timeout); an identical value is a no-op. The response reports,
  per field, whether it changed and its old/new values.
- **No confirmation step and no second-source cross-check before
  persisting.** The freshly-fetched Wikidata value is trusted and written
  synchronously in the same request — this is ADR-0032's existing trust
  model *re-applied later*, not superseded. Concretely, this rules out
  requiring per-field admin confirmation, requiring two independent
  Wikidata re-checks to agree before accepting a changed value, or routing
  the refreshed value through REQ-215/ADR-0053's existing
  `PlayerSuggestion` review queue.
- **No `reason` field.** Applying already-trusted source data is not a new
  manual judgment call — same category as REQ-503's "approve" action,
  which also requires no reason, unlike `PlayerOverride`'s REQ-501 path.
- **Audit trail is a structured `ILogger` line only** (admin id, player
  id, QID, per-field old/new values) — `Player` has no admin-audit columns
  (`Reason`/`LockedByAdminId`/`LockedAt`), and this REQ does not add any;
  same precedent as REQ-503's "remove" action, which also logs rather than
  writing a new audit row.
- `IPlayerRepository.GetPlayerForRefreshAsync` (tracked read) /
  `UpdatePlayerAsync` (save) exist for this one call site only — every
  other write path into `Player` is unaffected by this change and remains
  "set once at creation."

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Trust the refresh immediately, write synchronously, log only (chosen) | Small surface (one endpoint, one narrow repository exception); consistent with ADR-0032's already-accepted trust model; closes the "no correction path" gap with minimal new machinery | A newly-vandalized or still-wrong Wikidata snapshot at refresh time is accepted just as trustingly as the original write | The actual risk is a rare, admin-initiated, single-player correction — more machinery than that warrants, and reopening ADR-0032's trust model was not asked for |
| Require per-field admin confirmation before writing | A human catches a bad refresh before it's persisted | New UI/flow requirement not scoped by REQ-513; reintroduces a review step ADR-0032 already decided against | Rejected — inconsistent with the accepted trust model, adds scope |
| Require two independent Wikidata re-checks to agree before accepting a changed value | Reduces the chance of accepting a single bad/vandalized snapshot | Doubles query cost per refresh for a benefit that doesn't address a *sustained* bad value (vandalism visible across two near-simultaneous checks would still pass); no requested acceptance criteria call for this | Rejected — added complexity without closing the real gap |
| Route the refreshed value through the existing `PlayerSuggestion`/admin-approval queue (ADR-0053) | Reuses an existing review mechanism | That queue exists for suggestions about *cell answer-key* candidates the system hasn't independently verified yet, not for re-applying already-trusted source data to an existing `Player` row — a poor semantic fit | Rejected — wrong mechanism for this REQ's shape |
| Model corrections as a new `PlayerOverride`-style row instead of mutating `Player` in place | Reuses COMP-06's existing override-precedence mechanism (ADR-0015) | `PlayerOverride` exists for `PlayerAttribute` rows (category values), not `Player`'s own scalar columns; would need new plumbing with no existing precedent | Rejected — `PlayerOverride`'s semantics don't extend to this case |

## Consequences

- Positive: closes the "permanent corruption, no correction path" gap
  issue #239 reports, with a small, auditable surface — one endpoint, one
  repository exception, structured logging, no new data-entry error path.
- Negative / trade-offs accepted: a still-bad or newly-vandalized Wikidata
  snapshot at refresh time is accepted just as trustingly as the original
  write was — the only safeguard is an admin choosing to look at this
  specific player and the visible per-field diff in the response/log, not
  any independent verification.
- Follow-up: if vandalism/staleness recurs through this exact path at a
  meaningful frequency (rather than as a one-off), a stronger safeguard
  (per-field confirmation, or routing through a review queue) would need a
  new ADR superseding this one.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision
needs a new ADR that supersedes this one, or the approach needs to change.
Do not add a second write path into `Player.FullName`/`Position`/
`BirthYear`/`PhotoUrl` (e.g. from an automatic resync job, or a
non-admin-triggered caller) without a new ADR — `GetPlayerForRefreshAsync`/
`UpdatePlayerAsync` are scoped to REQ-513's one admin endpoint only. Do not
add a confirmation/review step to this endpoint on your own judgment
either — that would reopen this ADR's central trade-off, not extend it.
