# ADR-0060: An admin-commit action writes single-valued fields via `PlayerOverride`, multi-valued fields via additive `PlayerAttribute`

- **Status:** Accepted
- **Date:** 2026-08-08
- **Related requirements:** REQ-509, REQ-510, REQ-501
- **Related components:** COMP-06 (Data.PlayerStore), COMP-01-adjacent (`PlayerSuggestion`, `SubmittingUserId` reference)

## Context

S-090 (REQ-509/REQ-510) added an admin commit action that turns a
player-submitted suggestion (REQ-215) — or a standalone manual search
(REQ-510) — into real correctness-checking data. The backlog's own text
says a commit writes "through the existing `PlayerOverride`/`PlayerAttribute`
write path" without specifying which field goes through which table, and
ADR-0053 (which established this story's separate admin view) only says
committing "may only ever write `PlayerAttribute`/`PlayerOverride`, never
`PlayerNameIndex`" — it doesn't resolve the split between the two.

A suggestion (and a manual search commit) carries two kinds of confirmed
data with genuinely different cardinality:

- **Nationality** — single-valued. A player has exactly one.
- **Club(s)** — multi-valued. `PlayerSuggestion.AssertedClubs` is a list
  (REQ-215's own submission form allows 1+ entries — e.g. a club×club grid
  cell's suggestion asserts two distinct clubs), and REQ-113's "ever played
  for, at any career point" definition means a player can have an arbitrary
  number of legitimately-correct clubs simultaneously.

ADR-0015 already established that a `PlayerOverride` for `(PlayerId, Field)`
**replaces the entire attribute type** for correctness-checking — not one
value within it — and its own Consequences section flagged the exact
tension this story ran into: "for a multi-valued field (`club`), one
override silently makes every other cached value of that type ineffective,
even ones the override never mentioned." ADR-0015's Follow-up section
explicitly invited a future revisit "if a story... shows this is confusing
enough in practice to warrant [a] schema change." S-090 is that revisit —
committing a suggestion with two asserted clubs through a single
`PlayerOverride` would silently discard correctness for any other real club
the player has, including one the same commit is trying to confirm.

This is a genuinely new structural choice — not merely an elaboration of
ADR-0053's boundary (which is silent on the split) — because it sets a
reusable precedent for how any future admin-commit flow should route a
confirmed fact by its cardinality, and because it accepts new trade-offs
(below) that a future maintainer needs this ADR's reasoning to safely
revisit.

## Decision

`AdminSuggestionEndpoints.CommitPlayerDataAsync` (shared by REQ-509's
suggestion-commit and REQ-510's standalone commit) routes a commit's
confirmed values by field cardinality, not by a single uniform mechanism:

- **Nationality** (single-valued) → `PlayerOverride`, upserted: `Field =
  "nationality"`, `Value` = the confirmed nationality, with
  `Reason`/`LockedByAdminId`/`LockedAt` set exactly as REQ-501's existing
  manual-override path already sets them. This is a direct, intended use of
  ADR-0015's existing "replaces the whole type" semantics — nationality has
  only ever had one correct value, so full-type replacement is exactly
  right here.
- **Club(s)** (multi-valued) → additive `PlayerAttribute` rows
  (`AttributeType = "club"`), one per confirmed club not already effective
  for that player (checked via the existing `HasEffectiveAttributeAsync`),
  written through `IPlayerStoreRepository.AddPlayerAttributesBatchAsync` —
  the same additive mechanism Wikidata sync already uses to represent a
  player's multi-club history, deliberately bypassing `PlayerOverride`
  for this field so that confirming one club can never mask another.

Audit trail for the commit action itself (`admin_id`/timestamp, REQ-509's
"logged" requirement) is **not** carried by the `PlayerAttribute` rows
written this way (that entity has no such columns) — it lives on
`PlayerSuggestion.ResolvedByAdminId`/`ResolvedAt` for the suggestion-scoped
path, and on a structured `ILogger` line for REQ-510's standalone path
(which has no suggestion row to attach it to). The nationality write's own
audit trail is separately, redundantly carried by `PlayerOverride`'s
existing `Reason`/`LockedByAdminId`/`LockedAt` columns.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Split by cardinality: nationality → `PlayerOverride`, club(s) → additive `PlayerAttribute` (chosen)** | Correctly supports a player having more than one true club without any of them silently masking the others; reuses two mechanisms that already exist and are already trusted for their respective cardinalities | Two different write mechanisms for one logical "confirm this data" action; club writes carry no per-row `Source`/`Confidence`/audit metadata (see Consequences) | Chosen: the only option that doesn't either lose real club data or require a schema change neither REQ-509 nor REQ-510 asks for |
| Write everything through `PlayerOverride`, accepting ADR-0015's known limitation (one club value per player, full replacement) | One mechanism, full audit columns (`Reason`/`LockedByAdminId`/`LockedAt`) for every field | A suggestion asserting two clubs (a real, expected case for a club×club cell) could only ever have one committed without breaking the other's correctness-check — directly contradicts REQ-113's "ever played for, at any career point" and REQ-509's "fetch every club... at any career point" text | Rejected: would silently under-deliver on a stated acceptance criterion, not just accept a known trade-off |
| Extend `PlayerOverride`'s schema to support multiple values per `(PlayerId, Field)` (e.g. a delimited/JSON `Value`, or a new join table) | Single mechanism, full audit trail for every field, fixes ADR-0015's flagged limitation properly | A real schema change and a change to `HasEffectiveAttributeAsync`'s read-side contract, affecting every existing `PlayerOverride` caller (REQ-501's admin CRUD, S-009's guess-checking read) for a change this story alone doesn't need — Tier 0/1 scope discipline (`MVP-SCOPE.md`) argues against pulling this forward without a concrete need beyond this one commit flow | Deferred: ADR-0015's own Follow-up section already anticipated this as the "if it's confusing enough in practice" escalation; S-090's additive-`PlayerAttribute` route avoids needing it now without blocking a future revisit |
| Add audit columns (`Reason`/`AddedByAdminId`/`AddedAt`) directly to `PlayerAttribute` | Every write to that table would carry its own audit trail, closing the gap noted below | `PlayerAttribute` is also the table Wikidata sync writes in bulk (`WikidataLookupService`); every non-admin sync writer would need to either populate these columns meaninglessly or leave them null, diluting what the columns mean | Rejected: conflates two different writers' semantics on one shared table; `PlayerSuggestion.ResolvedByAdminId`/`ResolvedAt` (suggestion path) and a log line (standalone path) already satisfy REQ-509/510's actual "logged with admin_id and a timestamp" acceptance text without touching `PlayerAttribute`'s schema |

## Consequences

- Positive: a suggestion or manual search asserting more than one club
  commits correctly — every confirmed club stays independently effective
  for correctness-checking, matching REQ-113's definition, with no
  ADR-0015-style silent masking.
- Positive: no schema change to `PlayerOverride`/`HasEffectiveAttributeAsync`
  was needed, and every existing caller of both is unaffected.
- Negative / trade-off accepted: an admin-confirmed club `PlayerAttribute`
  row is now indistinguishable, at read time, from a routine Wikidata sync
  row — nothing on the row itself records that a human deliberately
  confirmed it via this flow. The only record of that fact lives on
  `PlayerSuggestion` (suggestion path) or in a log line (REQ-510's
  standalone path), not on the data row a future reader would actually
  query.
- Negative / trade-off accepted: there is no delete/correct path for a
  wrongly-added club `PlayerAttribute` row created this way — unlike
  `PlayerOverride`, which REQ-501 already gives full CRUD. Removing an
  incorrectly committed club today means going through
  `IPlayerStoreRepository`'s lower-level `PlayerAttribute` operations
  directly, not any admin-facing endpoint this story built.
- Negative / trade-off accepted, **not yet mitigated**: REQ-501's existing
  generic override-CRUD endpoint (`POST /admin/player-overrides`) accepts
  *any* `Field` string, including `"club"`. If an admin ever created a
  `club` override through that endpoint — a legitimate, unrestricted use of
  an already-shipped endpoint — `HasEffectiveAttributeAsync` would then
  ignore every `PlayerAttribute` club row this story's commit path adds for
  that player, per ADR-0015's existing full-type-replacement rule. Nothing
  in either endpoint's code guards against or warns about this interaction;
  it is assumed not to happen in practice (an admin using the generic
  override CRUD for `club` when a dedicated suggestion-review flow exists),
  not actually prevented.
- Follow-up: if `PlayerAttribute` ever needs its own audit trail for
  reasons beyond this story (e.g. a future "who added this club and why"
  admin view), revisit the rejected "audit columns on `PlayerAttribute`"
  alternative above with a concrete need driving it, rather than
  speculatively adding it now.
- Follow-up: the REQ-501 override-CRUD / this story's club-`PlayerAttribute`
  interaction above should be revisited if it's ever observed in practice
  (an admin's `club` override unexpectedly masking suggestion-confirmed
  clubs) — either by scoping `POST /admin/player-overrides`'s accepted
  `Field` values, or by having `HasEffectiveAttributeAsync` warn/flag the
  conflict, whichever a real occurrence turns out to need.

## Status note (2026-08-10, follow-up)

`ValidateCommitRequest` originally required `Reason` unconditionally,
regardless of which path(s) a commit actually wrote through. Combined with
this ADR's own §Decision — `Reason` is only ever persisted (on
`PlayerOverride`) for the nationality write; the additive `PlayerAttribute`
club write carries no audit columns at all, per this ADR's own Consequences
section — a clubs-only commit (no nationality) required an admin to type a
reason that was then validated and silently discarded, satisfying no actual
audit trail. Reported by a real admin user as unwanted friction with no
apparent purpose, which is exactly what it was for that path.

Fixed by making `Reason` conditionally required: still mandatory (and still
persisted to `PlayerOverride.Reason`) whenever a commit includes a
nationality; optional, and simply not collected as blocking, whenever a
commit is clubs-only. This does not reopen the "add audit columns to
`PlayerAttribute`" alternative rejected above — that remains a real gap
(a committed club still can't be traced to an admin/reason later) worth
revisiting only if a concrete need for it shows up, per this ADR's existing
Follow-up note. This status note only stops the UI/API from demanding input
that path had nowhere to put.

`AdminSuggestionEndpoints.cs`'s `ValidateCommitRequest` and
`SuggestionsScreen.tsx`'s `PlayerReviewPanel` (`canCommit`, the `Reason`
field's `required` attribute) were both updated together so client- and
server-side validation stay in sync, same as before this fix.
REQ-509/REQ-510's "a reason recorded" acceptance criterion is updated
alongside this note to say so explicitly, scoped to the nationality path.

## Status note (2026-08-17, S-129, backend half only)

This decision's write path (nationality → `PlayerOverride`, club(s) →
additive `PlayerAttribute`) is unchanged by this note — nothing here
reopens or contradicts the §Decision above. What changed is what
`CommitPlayerDataAsync`'s caller-facing `CommitPlayerDataResponse` reports
back about that write.

Before this story, `CommitPlayerDataResponse` echoed back the admin's own
confirmed `Nationality`/`Clubs` values — the exact list `CommitPlayerDataAsync`
computed as `confirmedClubs`, not necessarily identical to what was
requested, but still just "what ended up confirmed." That shape could not
distinguish a genuine write from a no-op: if every asserted club was
already an effective `PlayerAttribute` for that player (this ADR's own
`HasEffectiveAttributeAsync` skip path, §Decision above), the response
looked identical to a request where every club was newly written. Product
feedback (this story's own framing) was explicit: an admin needs to be
"100% sure a row was actually added to the DB," not just told back what
they asked for.

`CommitPlayerDataResponse` now reports the actually-changed facts
`CommitPlayerDataAsync` already computes internally but previously
discarded: `PlayerCreated` (true only if this `WikidataQid` had no
existing `Player` row before this call), `NationalityWritten` (true only
when the nationality branch actually ran — i.e. `request.Nationality` was
non-blank, so a `PlayerOverride` insert-or-update happened), and
`ClubsAdded`/`ClubsAlreadyEffective` — the same `alreadyEffective` check
this ADR's §Decision already routes through `HasEffectiveAttributeAsync`,
now surfaced as a partition instead of thrown away.

**Correction (same day, quality-gate finding):** the first version of
`PlayerCreated` was computed via a separate `GetPlayerByWikidataQidAsync`
pre-read before the existing `GetOrCreatePlayersByWikidataQidAsync`
upsert — non-atomic, and racy against exactly the kind of concurrent
caller this codebase already has for the same `WikidataQid` (REQ-211's
guess-time live-lookup fallback via `WikidataLookupService`,
`PlayerCareerPrefetchService`'s own batch sweep, or a second admin
commit): two concurrent first-ever inserts could both read "no existing
player" before either committed, so the loser would report
`PlayerCreated = true` for a request that actually wrote nothing. Worse,
`GetOrCreatePlayersByWikidataQidAsync` itself had no
`DbUpdateException`/unique-violation handling at all — unlike this
codebase's other get-or-create paths (`LeagueRepository
.GetOrCreateGlobalLeagueAsync`, `PathInstanceRepository
.GetOrCreateCycleStateAsync`) — so the losing concurrent insert against
`Player.WikidataQid`'s filtered unique index would throw a raw
`DbUpdateException`/500 instead of resolving to the winner.

Fixed by bringing `GetOrCreatePlayersByWikidataQidAsync` in line with that
same precedent: it now catches the unique-violation `DbUpdateException`,
detaches the losing insert(s), and re-fetches the winner, and its return
type changed from `IReadOnlyDictionary<string, Player>` to
`IReadOnlyDictionary<string, PlayerCreationResult>` (`PlayerCreationResult
(Player Player, bool WasCreated)`) so `WasCreated` is computed atomically
at the point of insert — including inside the new race-recovery path —
rather than via any separate read. `CommitPlayerDataAsync` now reads
`PlayerCreated` directly off that signal; the standalone pre-read is gone.
Existing high-volume callers (`WikidataLookupService`,
`PlayerCareerPrefetchService`) were updated to unwrap `.Player` where they
read the dictionary's values — their own behavior is otherwise unchanged.

No `ValidateCommitRequest` behavior, no write-path routing, and no
existing `PlayerOverride`/`PlayerAttribute` write changed — only what the
HTTP response communicates about writes that already happened exactly as
this ADR describes. Both `/admin/suggestions/{id}/commit` and
`/admin/player-search/commit` share the updated shape, same as they always
shared `CommitPlayerDataAsync` itself. Frontend consumption of the new
fields (`SuggestionsScreen.tsx` currently shows no confirmation message at
all on the main approval flow) is an explicit follow-up, not part of this
story.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

Specifically: any future admin-commit flow confirming a mix of
single-valued and multi-valued player facts should default to this same
split (single-valued → `PlayerOverride`, multi-valued → additive
`PlayerAttribute`) rather than inventing a third mechanism, unless a
concrete new requirement forces otherwise — and if it does, record that
divergence in a new ADR referencing this one, don't silently diverge.
