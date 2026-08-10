# ADR-0065: Site-wide announcement banner is a singleton table behind an unauthenticated public read

- **Status:** Accepted
- **Date:** 2026-08-10
- **Related requirements:** REQ-511
- **Related components:** COMP-13 (new)

## Context

REQ-511 asks for an admin-managed notice (maintenance windows, announcements)
that every visitor sees — explicitly including a fully logged-out visitor
who has never signed in, since a maintenance notice is exactly the kind of
thing someone needs to see *before* they try to log in. That requirement
forces two decisions that don't have an obvious, unique answer elsewhere in
this codebase:

1. **How does an unauthenticated visitor read it?** Every player-facing
   `GET` endpoint in this API requires a valid session
   (`.RequireAuthorization()`), following ADR-0017's JWT-validation
   convention — `GET /rounds/current`, `GET /players/autocomplete`, and
   every other read path a signed-in player uses. The only endpoint in the
   entire API today with no auth requirement at all is `GET /health`, a
   pure infrastructure probe never meant to carry product content. REQ-511
   needs a second one, but for player-visible product content this time —
   that's a real precedent-setting choice, not an obvious default.
2. **How many banners exist at once, and how is "the current one" found?**
   The obvious alternatives were: (a) a generic key/value settings table,
   reusable for future admin-configurable values beyond just this banner;
   (b) a list/queue of banner rows, each with an `IsActive` flag, where
   "the current banner" means "query for the most recent active row"; or
   (c) a true singleton — at most one row, ever, in its own table.

## Decision

1. `GET /announcement-banner` (`XGArcade.Api.Announcements.AnnouncementBannerEndpoints`)
   is registered with no `.RequireAuthorization()` call at all — the same
   registration style as `GET /health` — rather than any authenticated
   variant with a guest/anonymous carve-out. It returns only a boolean and
   a short text field, nothing sensitive, so the exposure is narrow and
   deliberate, not a general "make more things public" precedent.
2. `AnnouncementBanner` (`XGArcade.Data.Entities`) is modeled as a true
   singleton: at most one row ever exists, and `IAnnouncementBannerRepository`
   never inserts a second one — `UpsertMessageAsync` and `SetActiveAsync`
   both load-then-mutate the existing row (or create the first and only
   one if none exists yet). A second `PUT` never creates a second row; it
   replaces the first row's message in place. This is enforced by the
   repository's own method shapes, not just a documented convention a
   future caller could violate by adding a new method.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Authenticated read with a guest/anonymous-friendly carve-out (reuse Supabase's anonymous sign-in path, same as guest play) | Keeps "every read requires *some* session" as a universal rule | Still excludes a visitor who hasn't signed in at all yet (the exact audience a maintenance notice most needs to reach); adds a session-creation step just to read a banner | Contradicts REQ-511's own explicit "fully logged-out visitor" requirement |
| Generic key/value settings table | Reusable for any future admin-configurable value, not just this banner | No such table exists yet in this codebase; inventing one generically for a single current use is speculative, and REQ-511 doesn't ask for a general settings mechanism | No second admin-configurable value has been requested yet — building the general mechanism now would be designing for a hypothetical future need this REQ doesn't establish |
| List/queue of banner rows with `IsActive`, "current" = most recent active | Structurally supports multiple banners later without a schema change | REQ-511 is explicit that a second create/edit "replaces the single existing banner, it does not create an additional one" — a list model would need extra logic (deactivate-all-others-on-activate, or an ordering rule) purely to simulate single-banner behavior the requirement already asks for directly | Adds complexity to enforce a constraint (exactly one) that a true singleton gets for free from its own shape |

## Consequences

- Positive: a fully logged-out visitor can see a maintenance notice with
  zero session-creation overhead, matching REQ-511's actual intent. The
  singleton shape makes "exactly one banner" a structural guarantee, not a
  service-layer rule someone could accidentally bypass by adding a new
  write path later.
- Negative / trade-offs accepted: `GET /announcement-banner` is a second,
  precedent-setting unauthenticated endpoint — any future addition of
  another public, no-auth read should be weighed against this one rather
  than assumed to be equally safe by default, since the safety here rests
  on the response being narrow (a boolean and short text) and not on any
  general "public reads are fine" policy. The singleton model would need a
  real migration (not just a new row) if a genuine multi-banner need shows
  up later — accepted because REQ-511 explicitly scopes out concurrent
  banners for now, and speculative support wasn't worth the added
  complexity today.
- Follow-up: if a second admin-configurable value is requested later, this
  ADR's "why not a generic settings table" reasoning should be revisited —
  two independent singleton tables might be the sign a real settings
  mechanism is now worth building.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
