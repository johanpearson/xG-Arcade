# ADR-0087: Avatar image storage — Supabase Storage, client kept out of Core/Api

- **Status:** Accepted
- **Date:** 2026-08-24
- **Related requirements:** REQ-722
- **Related components:** COMP-01 (Core.Users, adjacent — avatar submissions
  are a player-profile concern, not a game-specific one)

## Context

REQ-722 (S-180, `docs/backlog.md` Epic 25) lets a player upload a profile
avatar image, held `Pending` until an admin approves it (REQ-517). That
image has to live somewhere durable. REQ-722's own text flags this as a
genuine "could reasonably have gone another way" decision — Supabase
Storage vs. Azure Blob Storage — and asks for the ADR that would normally
precede implementation, alongside it instead, per the 2026-08-24 planning
session's product direction.

Two questions needed answering, not one:

1. **Which storage provider** actually holds the uploaded bytes.
2. **Where the concrete client code for that provider lives**, given
   ADR-0004 already prohibits cloud-provider-specific code in
   `XGArcade.Core`/`XGArcade.Api`. This backend already has one precedent
   for "a Supabase-specific concrete client" —
   `XGArcade.Core.Auth.SupabaseAuthClient`, which lives directly inside
   `XGArcade.Core` (ADR-0013). That precedent predates ADR-0004's boundary
   being applied to a *new* Supabase integration, and copying it
   uncritically for avatar storage would just repeat whatever gap let the
   first one in — this ADR treats question 2 as its own decision, not an
   automatic "do what `SupabaseAuthClient` did."

## Decision

**Provider: Supabase Storage.** Product direction from the 2026-08-24
planning session: reuse the Supabase dependency this backend already has
(Supabase Auth, Supabase Postgres — ADR-0004, ADR-0013) rather than adding
Azure Blob Storage as a second, unrelated storage surface. This is a
product decision being recorded here, not relitigated — see "Alternatives
considered" below for the record only.

**Client placement: its own project, `XGArcade.Storage`, not
`XGArcade.Core`.** `IAvatarStorage` (the contract — upload an image, return
its storage key; best-effort delete a superseded one) lives in
`XGArcade.Core/Storage/IAvatarStorage.cs`, since it's just an interface,
referenced by DI the same way `XGArcade.Core.Auth.ISupabaseAuthClient` is.
The concrete implementation, `SupabaseAvatarStorage`
(`XGArcade.Storage/Supabase/SupabaseAvatarStorage.cs`), lives in a **new**
project, `XGArcade.Storage`, referencing only `XGArcade.Core` (for the
interface). `XGArcade.Api` references `XGArcade.Storage` and registers the
concrete type via `AddHttpClient<IAvatarStorage, SupabaseAvatarStorage>` in
`ServiceRegistration.cs`, mirroring how `XGArcade.DataSync`'s
`IWikidataClient`/`WikidataClient` are registered — interface consumed via
DI, concrete HTTP client isolated in its own project, neither
`XGArcade.Core` nor `XGArcade.Api` ever importing a provider-specific
namespace for it.

This deliberately does **not** reuse `SupabaseAuthClient`'s placement
inside `XGArcade.Core/Auth`. `XGArcade.DataSync` itself was ruled out too:
its own doc comment scopes it explicitly to football reference data
(Wikidata/API-Football, COMP-07) — folding an unrelated user-generated-
content storage client into it would blur that project's stated purpose
for no real benefit, the exact "don't pick an existing project that would
blur its purpose" case this ADR was asked to watch for.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Azure Blob Storage | Native to the Azure hosting stack this backend already deploys to (ADR-0004); mature SDK | A second, unrelated cloud storage account/credential to provision and operate, on top of Supabase (Auth + Postgres) already in use; no reuse of existing Supabase dependency | Product direction (2026-08-24): minimize infrastructure surface by reusing Supabase rather than adding a second, unrelated provider |
| `SupabaseAvatarStorage` inside `XGArcade.Core/Auth` (copying `SupabaseAuthClient`'s placement) | Zero new project, fastest to wire up; keeps every Supabase-specific client in one folder | Direct violation of ADR-0004's "no hosting/cloud-provider-specific code in `XGArcade.Core`/`XGArcade.Api`" applied literally — `SupabaseAuthClient`'s own placement there predates that rule being enforced against a *new* integration, and REQ-722/S-180 explicitly flag this as the boundary to get right this time, not a precedent to extend | Repeats the exact gap this ADR exists to close, rather than closing it |
| Fold `SupabaseAvatarStorage` into `XGArcade.DataSync` (the existing "external HTTP client kept out of Core" project) | No new project; DataSync already demonstrates the interface-in-Core/concrete-client-in-its-own-project pattern via `IWikidataClient`/`WikidataClient` | `XGArcade.DataSync`'s own doc comment (`XGArcade.DataSync.csproj`, `IWikidataClient.cs`) scopes it to football reference data ingestion (Wikidata now, API-Football at Tier 1) — avatar images are unrelated, user-generated content with a completely different lifecycle (write-once-per-upload vs. bulk reference-data sync) | Would blur an existing project's stated single responsibility rather than genuinely fitting it |
| New project, `XGArcade.Storage` (chosen) | Keeps `XGArcade.DataSync`'s scope intact; mirrors the same interface-in-Core/concrete-client-in-its-own-project shape `IWikidataClient`/`WikidataClient` already establish, applied to a genuinely different concern; trivial to swap providers later (a second implementation of `IAvatarStorage` in a sibling project, no `XGArcade.Core`/`XGArcade.Api` change) | One more `.csproj`/solution entry to maintain | The only option that satisfies ADR-0004's boundary without repeating `SupabaseAuthClient`'s pre-existing gap or blurring `XGArcade.DataSync`'s scope |

## Consequences

- Positive: `XGArcade.Core`/`XGArcade.Api` stay hosting-agnostic for this
  integration exactly as ADR-0004 requires — swapping Supabase Storage for
  a different provider later touches only `XGArcade.Storage` and one DI
  registration in `ServiceRegistration.cs`, never `XGArcade.Core`'s
  `IAvatarStorage` contract or any endpoint code; `XGArcade.DataSync`'s
  scope stays exactly what its own doc comments already claim.
- Negative / trade-offs accepted: a new project or a config value to
  operate that boundary properly (`Supabase:AvatarBucketName`, defaulting
  to `"avatars"`), the same modest per-integration cost every prior
  Supabase-backed feature (Auth, Postgres) already paid; `IAvatarStorage`'s
  contract must be kept intentionally narrow (upload + best-effort delete
  only) rather than growing to cover REQ-517/S-181's future "resolve a
  stored key into something servable" need speculatively — a small
  discipline cost, not a technical one.
- Follow-up (completed, 2026-08-24, S-181): `IAvatarStorage.
  GetPreviewUrlAsync(storageKey)` resolves a stored key into a short-lived
  (5 min) signed URL, generated server-side per request via `SupabaseAvatarStorage`
  (`POST /storage/v1/object/sign/{bucket}/{path}` with a
  `{"expiresIn": <seconds>}` body) — never a bare public URL, consistent
  with this ADR's "backend mediates" pattern for upload/delete. Used by
  `XGArcade.Api.Admin.AdminAvatarEndpoints`' `GET /admin/avatar-submissions`
  (REQ-517) to render an image preview in the moderation queue. The exact
  request/response shapes this ADR's implementation (`SupabaseAvatarStorage`)
  assumes for Supabase Storage's REST API (`POST /storage/v1/object/{bucket}/{path}`
  for upload, `DELETE /storage/v1/object/{bucket}` with a
  `{"prefixes": [...]}` body for bulk delete, and now `POST /storage/v1/
  object/sign/{bucket}/{path}` for a signed preview URL) are **not
  independently verified against a live Supabase project** from the
  sandbox this was built in (no network access to supabase.com) —
  flagged for manual verification against a real Supabase project before
  this ships, the same standing caveat `SupabaseAuthClient.cs` already
  carries for several of its own calls (`SignInAnonymouslyAsync`,
  `LinkEmailPasswordAsync`).
- Follow-up (completed, 2026-08-24, S-182, built in parallel with S-181 and
  merged afterward): `IAvatarStorage.DownloadAsync(storageKey)` resolves a
  stored key back into the raw image bytes + `ContentType`, streamed
  through this backend, for `GET /users/me/avatar/{id}/image`
  (`XGArcade.Api.Avatars.AvatarEndpoints`, REQ-722's "Seeing your own
  status" criterion) to serve the *owning player's own* preview of their
  own Pending/Rejected/Approved submission. This is a deliberate **second**
  mediation shape on `IAvatarStorage`, not a reuse of
  `GetPreviewUrlAsync` above — a signed URL handed to the client for this
  caller would violate ADR-0013's "backend mediates, frontend never talks
  to the provider directly" convention for what is, in this case, a
  general player-facing surface, not the admin-only queue where that
  tradeoff was accepted. `architecture-reviewer`'s review of the merged
  diff asked explicitly for this addendum, having found "two divergent
  designs" on one interface with no stated reconciliation; the two shapes
  now coexist by design, scoped to their own trust boundaries:
  - **`GetPreviewUrlAsync`** (signed URL) — admin reviewer only, via
    `AdminAvatarEndpoints`. Acceptable exposure because the caller is an
    already-privileged admin browser session, not a general player.
  - **`DownloadAsync`** (streamed bytes) — any authenticated player,
    scoped to rows they themselves own, via `AvatarEndpoints`. Never
    returns a URL a client could hand off or cache outside an
    authenticated request.

  **Canonical guidance for any future avatar-viewing surface** (e.g.
  REQ-411's stats view eventually rendering *another* player's `Approved`
  avatar, still unbuilt as of this note): that is neither of the two
  existing callers — not an admin, and not the image's own owner — so it
  needs its own explicit authorization decision (most likely: only ever
  serve an `Approved` row, via `DownloadAsync`'s streamed-bytes shape
  rather than a signed URL, extending `AvatarEndpoints`'s ownership check
  to "is `Approved`" instead of "is mine"). Do not default to
  `GetPreviewUrlAsync` for a new player-facing surface just because it
  already exists — that shape is reserved for the admin trust boundary
  that justified it here.

## For AI agents

`IAvatarStorage` (the interface) belongs in `XGArcade.Core/Storage/`;
any concrete implementation of it — Supabase Storage today, anything else
later — belongs in `XGArcade.Storage` (or a differently-named sibling
project with the same shape), never in `XGArcade.Core` or `XGArcade.Api`
directly, and never folded into `XGArcade.DataSync` (that project is
football-reference-data-scoped, not a general-purpose "external client"
dumping ground). If a future change seems to need Azure-specific or any
other cloud-provider-specific code inside `XGArcade.Core`/`XGArcade.Api`
to make avatar storage (or anything else) work, stop and flag it — that
would violate ADR-0004, not just this ADR. Do not treat
`XGArcade.Core.Auth.SupabaseAuthClient`'s existing placement inside
`XGArcade.Core` as a precedent to extend to new Supabase integrations —
this ADR is the correction, not a one-off exception.
