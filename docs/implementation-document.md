---
doc_id: implementation-document
title: Implementation Document
version: "0.59"
status: draft
last_updated: 2026-07-19
owner: Johan
related_docs:
  - requirements-document.md
  - architecture-document.md
id_prefix: IMPL
read_before: ["architecture-document.md", "requirements-document.md"]
update_when:
  - "A new library, service, or tool is adopted or replaced"
  - "Project structure or folder layout changes"
  - "A data model entity or field is added, renamed, or removed"
  - "Test strategy or tooling changes"
---

# Implementation Document – xG Arcade (working title)

Version 0.51 · 2026-07-17
References: `requirements-document.md`, `architecture-document.md`

> **Naming note:** "xG Arcade" is a placeholder for the overall product name.
> xG Grid is the first game hosted on it — see `requirements-document.md`
> §0. The root solution/repo is named after the xG Arcade; xG Grid lives
> in its own `XGArcade.Games.XGGrid` project, not the root namespace.

> **For AI agents:** this document defines HOW the system in
> `architecture-document.md` is concretely built (languages, frameworks, data
> model, algorithms, folder layout). If you change a technology choice, data
> model entity, or test tool, update this file in the same iteration. If a
> change also alters module boundaries or system structure, update
> `architecture-document.md` too and record the decision as an ADR under
> `docs/decisions/`.
>
> **This document describes the full system, not what's being built right
> now.** See `MVP-SCOPE.md` (repo root) for the actual build order — e.g.
> `ExternalApiUsage`, the API-Football fallback client, and
> `CountryDefinition`/`ClubDefinition`'s *dynamic* external-ID resolution
> (an admin-driven incremental flow for new clubs) are all Tier 1. The
> Wikidata client itself is Tier 0 (built in S-006, §6a) — Tier 0's fixed
> reference-table QIDs are just hand-looked-up and hardcoded rather than
> dynamically resolved.

## 1. Technology choices and rationale

| Layer | Choice | Rationale |
|---|---|---|
| Backend | C# / .NET 10 (LTS), ASP.NET Core Web API | Current LTS as of mid-2026 (released Nov 2025, supported to Nov 2028) — strongly typed, good testing tools (NUnit/xUnit), familiar from SpecOps. .NET 8's LTS window ends Nov 2026, so starting fresh on 10 avoids a near-term forced upgrade |
| Frontend runtime | Node.js 24 (Active LTS) | Current Active LTS as of mid-2026 (supported to Apr 2028); Node 22 is in Maintenance-only mode, Node 26 isn't LTS until Oct 2026 |
| Frontend | TypeScript + React 19 (Vite) | React 19 is current stable (19.2.x). Fast dev loop, large ecosystem, testable with Playwright |
| ORM | Entity Framework Core 10 (tracks the .NET version) | Migrations, testable via in-memory/test providers. Verify the Npgsql EF Core provider has a stable 10.x release at implementation time — it typically follows .NET's release within weeks, but confirm before committing |
| Database | PostgreSQL (Supabase, free tier) | Relational, handles junction tables (player×attribute) well, free to start |
| Frontend hosting | Azure Static Web Apps (Free tier) | Free at this scale, leverages existing Azure experience — see ADR-0004 |
| Backend hosting | Azure Container Apps (Consumption plan) | Generous always-free grant, standard container so it's easy to swap hosts later — see ADR-0004 |
| Container registry | GitHub Container Registry (GHCR) | Free, unlike Azure Container Registry which has no real free tier |
| IaC | Bicep, composed as modules under `/infra/bicep` | Matches existing Azure/Bicep experience and preference for composition over conditionals |
| Scheduling | GitHub Actions (cron) | Free for public repos, sufficient for sync and round-generation jobs |
| Auth | Supabase Auth | Bundled with the database, avoids a separate auth-provider integration — see ADR-0004 |
| Email | Resend | Custom SMTP for Supabase Auth's confirmation/reset emails, plus direct API calls from `Core.Notifications` for product notifications — see ADR-0005 |
| Backend test framework | NUnit + WebApplicationFactory | Matches an already-used pattern (SpecOps generates NUnit tests) |
| Frontend/UI test framework | Playwright + Vitest | Playwright for E2E/UI, Vitest for component/unit tests in TS |
| Web fonts | Google Fonts CDN (`fonts.googleapis.com`/`fonts.gstatic.com`), loaded via `<link>` tags in `frontend/index.html` | Serves the three typefaces `design-document.md` §2 specifies (Space Grotesk, Inter, IBM Plex Mono) without vendoring font files. Added S-010 when the first real screens shipped. This is a new runtime third party every visitor's browser talks to directly (not proxied through the backend) — `docs/legal/privacy-policy-draft.md`'s "Who we share it with" section was updated to name it in the same iteration this table was; if this later moves to self-hosted fonts, update both places again |

## 2. Data strategy: incremental cache, not upfront database

A common early question is whether to build a large database upfront, run
purely against live external data, or something in between. The chosen
approach is an **incremental, on-demand cache**:

- External football data sources (API-Football, Transfermarkt scrapers,
  Wikidata) are largely **player-centric** — they answer "what are Henry's
  attributes?" well, but generally cannot answer "give me all players who are
  French AND played for Arsenal" in a single call.
- Because of this, some local caching is unavoidable even in a "live-only"
  design — the intersection query has to happen locally regardless.
- Rather than pre-populating a large dataset before launch, the system only
  fetches and stores data for combinations that an actually-generated grid
  needs. The cache grows round by round, proportional to real usage.

This gives the best balance across perspectives:

- **User**: fast, consistent answer-checking; correctness doesn't shift
  mid-round if an external source updates its data
- **Developer**: no data-engineering project blocking v1 — the cache doubles
  as the mechanism already required for overrides (REQ-501)
- **Infrastructure**: storage stays small and grows only with actual usage,
  and repeated external API calls for the same lookups are avoided

## 3. Architecture overview

```
┌─────────────────────────┐        ┌──────────────────────────┐
│      Frontend (TS/React)│ <----> │  Backend API (.NET/C#)   │
│  - Grid UI               │  REST  │  - GridService            │
│  - Guess input            │  JSON  │  - ScoringService         │
│  - Leaderboards           │        │  - LeagueService          │
│  - Admin review UI        │        │  - PlayerDataService      │
└─────────────────────────┘        │  - RoundScheduler (job)   │
                                     └────────────┬──────────────┘
                                                  │ EF Core
                                     ┌────────────▼──────────────┐
                                     │      PostgreSQL (Supabase)│
                                     └────────────┬──────────────┘
                                                  │
                        ┌─────────────────────────┼─────────────────────────┐
                        │                         │                         │
              ┌─────────▼────────┐     ┌──────────▼─────────┐    ┌──────────▼─────────┐
              │  Sync job (GH     │     │ Live-lookup client  │    │  External source     │
              │  Actions, cron)   │     │ (on-demand fallback)│    │ Wikidata/API-Football│
              └───────────────────┘     └─────────────────────┘    └──────────────────────┘
```

**Design principle – platform for multiple games:** the backend is
structured as a "core" module (User, League, Round, Scoring) plus a pluggable
game-module boundary (`IGameModule`) that the first game (`GridGameModule`)
implements. New games implement the same interface without touching core.

```csharp
public interface IGameModule
{
    string GameKey { get; }
    Task<GameInstance> GenerateInstanceAsync(RoundConfig config);
    Task<ScoreResult> ScoreSubmissionAsync(Guid instanceId, Guid userId, object submission);
    // ADR-0021 (S-028): every cell id for a generated instance, regardless
    // of whether anyone ever guessed it — round-close uses this to find
    // (and penalize) a round participant's unattempted cells, without
    // Core reaching into a game-specific instance table directly.
    Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId);
}
```

**Security middleware pipeline** (applies to every request, regardless of
game module — realizes REQ-606):

```
HTTPS redirection
  → CORS (restricted to the known frontend origin(s), not a wildcard)
  → Rate limiting (ASP.NET Core's built-in rate limiting middleware; tighter
    policy specifically on /auth/* and /internal/test-data/* than on
    general endpoints)
  → JWT validation (Supabase Auth token; populates the authenticated user)
  → Admin authorization: admin = the authenticated user's Supabase user id
    appears in an `Admin__UserIds` environment variable (comma-separated).
    Config-based, not a database role — deliberately the simplest thing
    that works for a solo-operated Tier 0; revisit only if there's ever
    more than a couple of admins. Admin-only endpoints check this claim,
    never a hardcoded email.
  → Authorization policy check (Player vs Admin, per-endpoint)
  → Controller action
```

**Tier 0 status (S-004, updated S-012):** HTTPS redirection, CORS, JWT
validation, and admin authorization are wired in `Program.cs` today, in
that order (`UseHttpsRedirection` → `UseCors("Frontend")` →
`UseAuthentication` → `UseAuthorization` → `MapControllers`, with the
"Admin" policy — `AdminRequirement`/`AdminAuthorizationHandler`,
`XGArcade.Api.Auth.AdminAuthorization.cs` — registered alongside
`AddAuthorization` and applied per-endpoint via `.RequireAuthorization
("Admin")` on every `/admin/*` route in `XGArcade.Api.Admin
.AdminEndpoints`, S-012). `AdminAuthorizationHandler` re-parses
`Admin:UserIds` (env var `Admin__UserIds`, comma-separated GUIDs) on every
request rather than caching it — see that class's own doc comment for why
this is a deliberate Tier 0 simplicity choice, not an oversight. Rate
limiting is still not implemented — no story has wired ASP.NET Core's rate
limiting middleware yet, so that one step of the pipeline above remains the
full/long-term target, not current behavior.

JWT validation specifics as actually implemented: `AddJwtBearer` sets
`MapInboundClaims = false` (keeps claim types as Supabase issues them —
`sub`, `role`, etc. — instead of ASP.NET Core's legacy remap to long
XML-SOAP claim URIs), validates the issuer as `{Supabase:Url}/auth/v1` and
the audience as `"authenticated"`, and validates the signature against
Supabase's JWKS endpoint (`{Supabase:Url}/auth/v1/.well-known/jwks.json`
by default, overridable via `Auth:SupabaseJwksPath`) via a custom
`SupabaseJwksConfigurationRetriever` feeding a
`ConfigurationManager<OpenIdConnectConfiguration>` — Supabase's JWT
Signing Keys system issues rotating asymmetric keys identified by a `kid`
header claim, not a static shared secret (see ADR-0017; a real deployment
failing with `IDX10503`/"Number of keys in Configuration: '0'" is what
surfaced the original static-HS256-secret assumption as wrong). A
test-only branch, `Auth:Mode=local-e2e`, swaps in a locally-signed JWT
(`LocalE2EAuth`'s fixed signing key/issuer/audience, HS256, purely
in-process) instead of Supabase's — gated by
`builder.Environment.IsDevelopment()` checked directly in `Program.cs`
alongside the config flag, never by the config flag alone (the same
never-guarded-only-by-config discipline ADR-0006 established for COMP-09).
See ADR-0013 and ADR-0017.

`Testing.SeedManager` (COMP-09) endpoints are only added to the routing
table when `ASPNETCORE_ENVIRONMENT != Production`, checked in `Program.cs`
before endpoint registration — not as an attribute that could be
misconfigured per-endpoint. See ADR-0006.

## 4. Project structure

```
/backend
  /src
    /XGArcade.Api              -> Controllers, DTOs, Program.cs
    /XGArcade.Core             -> User, League, Round, Scoring, Notifications (shared domain)
    /XGArcade.Games.XGGrid       -> GridGameModule, category logic, generator
    /XGArcade.Data             -> EF Core DbContext, migrations, repositories
    /XGArcade.DataSync         -> Wikidata/API-Football clients, sync jobs
    /XGArcade.Email            -> Resend API client, shared by Core.Notifications
                                   (Supabase Auth's own emails are configured
                                   via its dashboard/SMTP settings, not this project)
    /XGArcade.Testing          -> Testing.SeedManager (COMP-09): test-data
                                   create/reset/scenario logic, referenced by
                                   XGArcade.Api's conditionally-registered
                                   /internal/test-data/* endpoints only
  /tests
    /XGArcade.Core.Tests       -> NUnit unit tests (scoring, override merge)
    /XGArcade.Games.XGGrid.Tests -> NUnit unit tests (grid generation, validation)
    /XGArcade.Data.Tests       -> NUnit unit tests (repositories, EF Core model config)
    /XGArcade.DataSync.Tests   -> NUnit unit tests (sync clients, mocked HTTP)
    /XGArcade.Api.Tests        -> API tests (WebApplicationFactory + in-memory/testcontainer DB)

/frontend
  /src                          -> feature folders, not the layer folders this
                                   section originally sketched (S-010 built
                                   /auth, /grid; /lib holds the hand-written
                                   fetch API client + shared types/rules, not
                                   an OpenAPI-generated one as first guessed).
                                   Component tests are co-located next to
                                   their component (*.test.tsx under /src),
                                   per docs/coding-guidelines.md, not kept in
                                   a separate /tests/unit tree.
    /auth                        -> AuthScreen (login/signup, REQ-701),
                                     DeleteAccountScreen (SCREEN-05, REQ-710,
                                     S-039)
    /games                        -> GameSelectScreen (REQ-303's S-021 UX
                                     addition: post-login/post-signup landing
                                     screen, one static tile for xG Grid —
                                     no backend "list games" endpoint;
                                     Tier 0 only ever has one game)
    /grid                        -> GridScreen, Grid, GridCell, CellState,
                                     GuessInput, CategoryLabel,
                                     ScoringExplainer (SCREEN-01/01a/02/06,
                                     S-041)
    /leaderboard                 -> LeaderboardScreen (SCREEN-03, REQ-401/404's
                                     Tier 0 slice — added S-011, global league only)
    /nav                          -> HeaderNav (SCREEN-07, REQ-712: mobile-only
                                     hamburger toggle collapsing the header nav
                                     below 480px)
    /settings                     -> SettingsScreen (SCREEN-08, REQ-713: the
                                     "Settings" nav entry's destination, wraps
                                     /auth's DeleteAccountScreen unchanged plus
                                     an admin-only link to /admin's AdminScreen)
    /lib                          -> api.ts (typed fetch client), types.ts,
                                     categoryDisplay.ts, guessRules.ts,
                                     scoringRules.ts (MAX_POINTS_PER_CELL,
                                     added 2026-07-14 bugfix)
  /tests
    /unit                       -> Vitest — mostly the pre-S-010 App/health-check
                                   test; App.tsx's own top-level routing tests
                                   (e.g. REQ-303's game-selection landing/nav
                                   cases, S-021) also live here since App.tsx
                                   itself isn't under a feature folder. Newer
                                   component tests live under /src (see above)
    /e2e                        -> Playwright (full user flows)

/infra
  /github-workflows             -> ci.yml, sync-players.yml, generate-round.yml
```

## 5. Data model

Entities below are grouped by ownership: xG Grid game entities first,
then xG Arcade (Core) entities. This grouping matters — see the note on
`Round` and `Guess` below regarding ADR-0003.

```csharp
public class Player
{
    public Guid Id { get; set; }
    public string FullName { get; set; }
    // Auto-maintained by FullName's setter (S-009) via the shared
    // PlayerNameNormalizer.Normalize function below — never assigned
    // directly by callers. REQ-208's Tier 0 "simple half" guess-time name
    // matching (GetPlayersByNormalizedFullNameAsync) queries this column
    // directly; no PlayerNameIndex/COMP-10 in Tier 0 (MVP-SCOPE.md).
    public string NormalizedFullName { get; set; }
    // Dedup identity: the same player returned by two different
    // intersection queries (France×Arsenal and Brazil×Barcelona, say) must
    // upsert into ONE row, keyed on this — never insert-blindly per query.
    // Nullable only for a future non-Wikidata source (Tier 1); unique
    // index where not null.
    public string WikidataQid { get; set; }
    // REQ-214 (S-043, backend half): Wikidata's P18 (image), carried
    // through the same intersection queries as FullName/WikidataQid and
    // set once at player creation — never re-synced on a later lookup,
    // same as FullName. Null whenever Wikidata has no P18 for this player
    // (a normal case, never an error). Deliberately a Player column, NOT a
    // PlayerAttribute row — PlayerAttribute's composite key holds many
    // rows per player (one per career club/nationality/trophy), so a
    // per-player scalar has no natural "which row owns it" answer there.
    // Migration: AddPlayerPhotoUrl.
    public string PhotoUrl { get; set; }
}

// PlayerData, PlayerOverride, and PlayerAttribute below each carry a
// foreign key to Player.Id with cascade delete. Unlike Round's deliberate
// FK omission toward GridInstance (ADR-0003, a cross-component boundary),
// these three live inside the same component (COMP-06) as Player, so
// there's no boundary reason to leave them unconstrained — a row pointing
// at a nonexistent Player is just bad data.
public class PlayerData          // raw, per source
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Field { get; set; }     // e.g. "nationality", "club"
    public string Value { get; set; }
    public string Source { get; set; }    // "wikidata" | "api_football" | "live_lookup"
    public string Confidence { get; set; } // "verified" | "unverified"
    public DateTime SyncedAt { get; set; }
}

public class PlayerOverride       // manual, always wins
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Field { get; set; }
    public string Value { get; set; }
    public string Reason { get; set; }
    public Guid LockedByAdminId { get; set; }
    public DateTime LockedAt { get; set; }
}

public class PlayerAttribute      // effective, denormalized for fast querying
{
    public Guid PlayerId { get; set; }
    public string AttributeType { get; set; }   // "club" | "nationality" | "trophy"
    public string AttributeValue { get; set; }
}

// Broad, bulk-imported, deliberately separate from PlayerAttribute above —
// see ADR-0007. Used ONLY for autocomplete and name matching, NEVER for
// correctness-checking. Refreshed periodically as a whole, not built
// incrementally.
//
// Built S-032 exactly as sketched below, with one implementation note not
// obvious from the shape alone: there is deliberately no WikidataQid column
// here (unlike Player.WikidataQid in COMP-06) — PlayerNameIndexImporter
// instead derives PlayerId as a deterministic hash of the QID (MD5's 16
// bytes mapped onto a Guid), so re-running the bulk import updates the same
// row in place rather than minting a fresh random PlayerId — and duplicating
// the row — on every re-run. See PlayerNameIndexImporter's own doc comment
// (XGArcade.DataSync.Wikidata) for the full reasoning.
public class PlayerNameIndex
{
    public Guid PlayerId { get; set; }              // synthetic, QID-derived key local to this table (PlayerNameIndexImporter.DeterministicPlayerId) — NOT the same id space as PlayerAttribute's PlayerId (Player.Id is a plain Guid.NewGuid()); reconciling the two per real person is unbuilt, out of scope for S-032
    public string PrimaryName { get; set; }
    public string NormalizedName { get; set; }      // lowercased, diacritics stripped — see REQ-208
    public int? BirthYear { get; set; }             // disambiguation display only
    public string PrimaryNationality { get; set; }  // disambiguation display only
    // PhotoUrl was sketched here originally and shipped in S-032, then
    // dropped 2026-07-18 (RemovePlayerNameIndexPhotoUrl migration): the
    // autocomplete contract never exposed a photo, so fetching P18 and
    // storing the column was dead weight. The photo feature that
    // eventually did ship (REQ-214, S-043) lives on Player.PhotoUrl
    // (COMP-06) instead, not here — this column stays gone; do not
    // re-add it without a new, real autocomplete-photo requirement.
}

public class PlayerAlias          // known nicknames/stage names, e.g. "Kaká"
{
    public Guid PlayerId { get; set; }
    public string Alias { get; set; }
    public string NormalizedAlias { get; set; }
}

// v1 category types are Country, Club, Trophy (REQ-108). Trophy is
// reference data, not hardcoded — adding a new recognized trophy is a row
// insert, not a code change.
public class TrophyDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; }          // e.g. "FIFA World Cup", "Ballon d'Or"
    public bool IsTeamTrophy { get; set; }    // team competition vs. individual award —
                                                // informs display copy, not matching logic
    public string WikidataQid { get; set; }   // nullable; resolved manually, small table (ADR-0012)
}

// Category value reference tables (ADR-0012, REQ-109) — the source of
// truth for what grid generation can pick from, and the place external
// IDs are cached once resolved. Grid generation picks from these tables
// directly, never derives values ad hoc from PlayerAttribute.

// Bulk-seeded once (a deliberate, narrow exception to ADR-0001 — countries
// are a small, extremely stable ~200-row set, resolved via Wikidata's
// P297 ISO 3166-1 alpha-2 property in one bulk query).
public class CountryDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string WikidataQid { get; set; }   // nullable until resolved
}

// NOT bulk-seeded — added incrementally when an admin adds a new club as
// an allowed category value (via the same admin flow as REQ-503). At that
// moment, WikidataQid and ApiFootballTeamId are resolved once and cached.
public class ClubDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string WikidataQid { get; set; }        // nullable until resolved
    public int? ApiFootballTeamId { get; set; }    // nullable until resolved
}

// Phase 2 — deferred (see requirements-document.md §6). Not built in v1;
// v1 uses placeholder initial-badges only. Included now so the shape
// exists before this is built, same pattern as NotificationPreference.
// Crest imagery, sourced from a data provider (API-Football) per ADR-0008.
// Cached exactly like PlayerData (ADR-0001's philosophy applied to a
// different kind of data): fetched once per club, reused forever, never
// re-fetched speculatively. Genuinely low-risk when built: API-Football's
// own docs confirm logo/crest calls don't count against the request quota
// at all, and the universe of distinct clubs ever used as a category
// value is small and largely static (a few hundred well-known clubs) —
// nothing like the much larger space of individual player lookups.
public class ClubCrest
{
    public string ClubName { get; set; }      // matches the club category value used elsewhere
    public string CrestUrl { get; set; }
    public string Source { get; set; }        // "api_football"
    public DateTime FetchedAt { get; set; }
}

// Tracks daily usage per external source (ADR-0011). Wikidata usage is
// tracked for observability only — it isn't meaningfully capped for this
// system's query volume, so it's never gated. Only the api_football row's
// count is checked against GuessTimeLookupThreshold, since that's the
// actually-constrained fallback source (100 requests/day on the free tier).
public class ExternalApiUsage
{
    public string Source { get; set; }        // "wikidata" | "api_football"
    public DateOnly Date { get; set; }
    public int RequestCount { get; set; }
    // Only applies to the "api_football" row. Guess-time lookups stop
    // falling back to API-Football once RequestCount crosses this on the
    // days Wikidata didn't resolve the lookup — default 80, reserving 20
    // of the 100/day cap for scheduled grid generation. See ADR-0011 for
    // why API-Football is now a rarely-touched fallback, not a coequal source.
    public const int GuessTimeLookupThreshold = 80;
}

// --- xG Grid game entities (owned by Games.XGGrid, COMP-05) ---
// These are internal to the xG Grid module conceptually — Core never
// references them directly (ADR-0003), and another game would define its
// own equivalent instance entities without touching any of the types
// below. As EF Core classes they're physically defined in XGArcade.Data
// alongside every other component's entities, in the one shared
// XGArcadeDbContext — see ADR-0014 for why "owned by" doesn't mean
// "defined in that component's own project."

public class GridTemplate
{
    public Guid Id { get; set; }
    public int Size { get; set; }             // 3, 4, 5
    public List<string> AllowedCategoryTypes { get; set; }
}

public class GridInstance
{
    public Guid Id { get; set; }              // this is the value stored as Round.GameInstanceId
    public Guid TemplateId { get; set; }
    public List<GridCell> Cells { get; set; }
}

public class GridCell
{
    public Guid Id { get; set; }
    public Guid GridInstanceId { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    // RowCategoryType/ColCategoryType ("country" | "club") were added in
    // S-007, beyond this section's original illustrative shape: Tier 0
    // generates either Country x Club or, as of S-030, Club x Club
    // (MVP-SCOPE.md) — recording the type per cell (rather than assuming a
    // fixed axis) is what lets guess-checking (S-009) know whether to query
    // PlayerAttribute's "nationality" or "club" AttributeType for a given
    // cell without re-deriving it, and keeps the schema correct once a
    // future Tier 1 grid mixes in further category types (REQ-108's
    // Trophy) across an axis.
    public string RowCategoryType { get; set; }
    public string RowCategoryValue { get; set; }
    public string ColCategoryType { get; set; }
    public string ColCategoryValue { get; set; }
}

// --- Core (xG Arcade) entities (XGArcade.Core) ---
// Game-agnostic. Round deliberately holds no foreign key to GridInstance or
// any other game-specific table — see ADR-0003. Like User below and the xG
// Grid entities above, these classes are physically defined as EF Core
// entities in XGArcade.Data (alongside IRoundRepository/RoundRepository),
// not inside XGArcade.Core itself — "(XGArcade.Core)" names where the
// business/orchestration logic that owns them lives (Core.Rounds'
// RoundGenerationService/RoundCloseService), not where the class file sits.
// See ADR-0014.

// Password credentials live in Supabase Auth, not here — this table only
// mirrors the minimal profile/state XGArcade.Core needs. See ADR-0004/0005.
public class User
{
    public Guid Id { get; set; }
    public Guid AuthProviderUserId { get; set; }  // Supabase Auth's user id
    public string Email { get; set; }
    // Added S-011 (REQ-401/404/701): the only identity a leaderboard shows
    // another player — collected at signup, 1-30 chars, required. Rows that
    // predate this column were backfilled (UserDisplayNameBackfiller) from
    // the email's local part (before "@").
    public string DisplayName { get; set; }
    // Added S-017 (REQ-701): lowercase-folded mirror of DisplayName, kept in
    // lockstep by the DisplayName setter (User.NormalizeCase is the one
    // place "case-insensitive" is defined, called by both the entity setter
    // and UserRepository.DisplayNameExistsAsync). Backs the unique index
    // below. Pre-existing rows that collided case-insensitively were
    // resolved by migration 20260711203352_AddDisplayNameUniqueness before
    // the index was added — see ADR-0019.
    public string NormalizedDisplayName { get; private set; }
    public bool EmailConfirmed { get; set; }       // mirrors Supabase Auth's confirmed state; see REQ-702
    public DateTime CreatedAt { get; set; }
}

// Deferred to Phase 2 alongside REQ-706 — included now so the shape exists
// before Core.Notifications is built, not because it's implemented yet.
public class NotificationPreference
{
    public Guid UserId { get; set; }
    public bool RoundResultsOptIn { get; set; }   // default TBD — see requirements-document.md §4.7 open question
}

public class Round
{
    public Guid Id { get; set; }
    public string GameKey { get; set; }        // e.g. "xg-grid"; used to resolve the owning IGameModule
    public Guid GameInstanceId { get; set; }   // opaque to Core; meaningful only to the owning game module
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool AllowGuessChange { get; set; }
    // REQ-408 (S-054, migration AddRoundClosedAt): null until
    // RoundCloseService.CloseRoundAsync's score-locking step (REQ-205)
    // completes successfully — set only after, never before or
    // concurrently, so a reader can never see this non-null while some of
    // the round's guesses still have FinalPoints == null. First-close-wins,
    // never overwritten by a later CloseRoundAsync call. Executes ADR-0022's
    // own "Follow-up" section, which named this exact column/trigger in
    // advance — no new ADR needed. GetClosedRoundLeaderboardAsync
    // (Core.Leagues) gates purely on this field.
    public DateTime? ClosedAt { get; set; }
}

// Note: Guess.CellId currently references a GridCell, which technically
// couples this Core entity to the xG Grid game — the same issue ADR-0003
// fixed for Round. This is an accepted simplification for v1 (only one game
// exists), not a precedent to copy. When a second game module is built,
// generalize this the same way: e.g. Guess.SubmissionRef (opaque, resolved
// by the owning game module) instead of a typed CellId. Track this as a
// follow-up rather than solving it speculatively now.
public class Guess
{
    public Guid Id { get; set; }
    public Guid RoundId { get; set; }
    public Guid? UserId { get; set; }   // nullable: null after account deletion
                                          // anonymizes this row per REQ-710,
                                          // without deleting it (other users'
                                          // uniqueness scores depend on the
                                          // total guess count staying intact)
    public Guid CellId { get; set; }
    // REQ-201's "answer" — the raw text the player typed, kept even when it
    // matched no candidate at all, so a rejected guess can still be
    // spot-checked later (REQ-211's Tier 1 trigger, MVP-SCOPE.md). Added
    // S-009, beyond this section's original illustrative shape.
    public string SubmittedName { get; set; }
    // Nullable, unlike this section's original illustrative shape (S-009
    // fix): an incorrect guess has no real player to point at, so this is
    // null whenever IsCorrect is false and no candidate matched at all.
    public Guid? PlayerAnswerId { get; set; }
    public bool IsCorrect { get; set; }
    public int AttemptCount { get; set; }                // REQ-210, capped at 2
    public double? FinalUniquenessScore { get; set; }   // null until the round closes
    public int? FinalPoints { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class League
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }          // "global" | "custom" — Tier 0
                                                // (S-011) only ever writes
                                                // "global"; a filtered
                                                // unique index enforces at
                                                // most one such row exists
    public string? InviteCode { get; set; }   // Tier 1 (REQ-402), always null for "global"
    public Guid? CreatedByUserId { get; set; } // Tier 1 (REQ-402), always null for "global"
}

public class LeagueMembership
{
    public Guid LeagueId { get; set; }
    public Guid UserId { get; set; }
}
```

**Required indexes** (REQ-607) — configured via EF Core `HasIndex` in
`XGArcade.Data`'s model configuration, not left to default behavior:

| Table | Index | Reason |
|---|---|---|
| `Guess` | `(CellId)` | Uniqueness calculation (REQ-204) counts/groups by cell on every read |
| `Guess` | `(RoundId, UserId, CellId)` unique | Built in S-009 as `(RoundId, UserId, CellId)`, not the `(RoundId, UserId)` this row originally said — the extra `CellId` column is what actually enforces the "one active guess per cell per round" check (REQ-201); a plain `(RoundId, UserId)` index alone can't be unique (a user has many guesses per round, one per cell). Also serves REQ-206's total-score lookup as its leading-columns prefix |
| `LeagueMembership` | `(LeagueId, UserId)` composite/unique (also the primary key) | Leaderboard queries filter by league; also enforces no duplicate membership |
| `League` | `(Type)` filtered unique, `WHERE Type = 'global'` | Built S-011: guards `LeagueRepository.GetOrCreateGlobalLeagueAsync`'s check-then-insert against a concurrent double-create of the singleton global league (REQ-401) |
| `PlayerAttribute` | `(AttributeType, AttributeValue)` | Grid generation's candidate-matching query (REQ-101) |
| `Player` | `(NormalizedFullName)` | Built in S-009, beyond this table's original scope: REQ-208's Tier 0 guess-time name matching looks this up directly (no `PlayerNameIndex` in Tier 0 — see REQ-208's status note) |
| `PlayerNameIndex` | `(NormalizedName)` | Built S-032 — `HasIndex(NormalizedName)` on `PlayerNameIndexEntries` (the EF Core table name, keyed by `PlayerId`), backing `IPlayerNameIndexRepository.SearchByPrefixAsync`'s autocomplete prefix search (REQ-207). REQ-208's "every guess submission normalizes and looks up against this first" is still not built — S-032 only wired autocomplete, not guess-time name matching, to this table |
| `PlayerAlias` | `(NormalizedAlias)` | Alias lookup on the fallback path when the primary name doesn't match (REQ-208) |
| `ExternalApiUsage` | `(Source, Date)` unique | Checked on every guess-time live-lookup candidacy check (REQ-211); must be fast since it's in the hot guess-submission path |
| `CountryDefinition` / `ClubDefinition` / `TrophyDefinition` | `(Name)` unique | Grid generation picks from these directly (REQ-109); uniqueness also prevents an admin accidentally adding the same club twice under slightly different casing |
| `User` | `(AuthProviderUserId)` unique | Every authenticated request resolves this first |
| `User` | `(NormalizedDisplayName)` unique | Built S-017 (REQ-701): the DB-level backstop behind `AuthController.Signup`'s `DisplayNameExistsAsync` pre-check, closing the race window between two concurrent signups choosing the same display name (any casing) — `UserRepository.AddAsync` catches the resulting `Npgsql.PostgresException` and throws `DisplayNameAlreadyInUseException`. See ADR-0019 for how pre-existing collisions were resolved before this index could be added |

## 6. Core algorithms

**Shared live-lookup waterfall (ADR-0011)** — used by both grid generation
(REQ-103) and guess-time verification (REQ-211); defined once so both call
sites can't drift into different source orderings:

```
function live_lookup(player_or_candidates, category_a, category_b):
    // REQ-109/ADR-0012: Wikidata needs each category's resolved QID —
    // a null QID (not yet resolved, e.g. a newly-added club) skips
    // Wikidata entirely and goes straight to the fallback, no error
    if category_a.WikidataQid != null AND category_b.WikidataQid != null:
        result = query_wikidata(player_or_candidates, category_a.WikidataQid,
                                 category_b.WikidataQid, timeout: 15s)
        if result.resolved:
            ExternalApiUsage.increment("wikidata", today)  // observability only, never gates
            return result
    // Wikidata skipped (no QID) or timed out/errored/no-match — fallback:
    if ExternalApiUsage.count("api_football", today) >= ExternalApiUsage.GuessTimeLookupThreshold:
        return unresolved   // budget exhausted — caller fails closed
    result = query_api_football(player_or_candidates, category_a, category_b)
    ExternalApiUsage.increment("api_football", today)
    return result
```

**Name matching and disambiguation (REQ-207, REQ-208, REQ-209, REQ-210, REQ-211)**

```
normalize(s) = lowercase(strip_diacritics(strip_punctuation(NFKD(s)))).trim().collapse_whitespace()

on guess submission with typed/selected name N (isDisambiguationResolution: bool):
    if existingGuess.IsCorrect == true:
        reject with "this cell is already solved"          // REQ-210
    if existingGuess.AttemptCount >= 2 AND NOT isDisambiguationResolution:
        reject with "no attempts remaining for this cell"   // REQ-210
    normalizedN = normalize(N)
    candidates = PlayerNameIndex WHERE NormalizedName = normalizedN
              UNION PlayerAlias WHERE NormalizedAlias = normalizedN
    if candidates is empty:
        candidates = fuzzy_search(normalizedN, PlayerNameIndex, maxEditDistance: 2)
        if candidates is empty: → guess is incorrect, no valid player found at all
        if fuzzy match confidence is low / candidate set is large: → incorrect
          rather than guessing on the player's behalf

    matchingCandidates = candidates WHERE satisfies(cell.rowCategory)
                                       AND satisfies(cell.colCategory)
                                       -- checked via Data.PlayerStore (COMP-06),
                                       -- effective data, override-aware

    if matchingCandidates.count == 0 AND candidates.count == 1:
        // REQ-211 / ADR-0011: the single candidate matched PlayerNameIndex
        // (a real player) but PlayerStore has no data at all confirming or
        // denying either category — this is the "known but unverified" gap,
        // not necessarily a wrong guess
        candidate = candidates[0]
        if NOT PlayerStore.hasAnyRecordFor(candidate, cell.rowCategory.type, cell.colCategory.type):
            result = live_lookup(candidate, cell.rowCategory, cell.colCategory)  // waterfall, see below
            if result.resolved: persist as PlayerData(unverified)     // same request, never deferred
            matchingCandidates = candidates WHERE satisfies(...) AND satisfies(...)  // re-check
            // if unresolved (Wikidata failed AND API-Football budget exhausted):
            // falls through, evaluated as incorrect below using only existing cached data

    if matchingCandidates.count == 0:
        if NOT isDisambiguationResolution: AttemptCount += 1   // counts as a used attempt
        → guess is incorrect (REQ-203), shown immediately
    if matchingCandidates.count == 1:
        if NOT isDisambiguationResolution: AttemptCount += 1
        → accept automatically (REQ-209), IsCorrect = true, cell locks (REQ-210)
    if matchingCandidates.count > 1:
        → return disambiguation prompt (does NOT increment AttemptCount yet —
          the attempt is counted only once the player resolves it, via a
          resubmission with isDisambiguationResolution = true)
```

`satisfies(category)` reuses exactly the same effective-data check as
REQ-101/203 (`PlayerAttribute` merged with `PlayerOverride`) — disambiguation
doesn't introduce a second correctness rule, it just applies the existing
one to more than one candidate at once.

**Tier 0 status (S-009, extended S-011/ADR-0018):** the pseudocode above is
the full/long-term shape. What `GuessSubmissionService`
(`XGArcade.Core.Scoring`) + `GridGameModule.ScoreSubmissionAsync`
(`XGArcade.Games.XGGrid`) actually implement, real and tested:

- The two REQ-210 lock/attempt-cap checks at the top, exactly as written —
  performed in `Core.Scoring`, before `IGameModule` is ever called.
- `normalize(N)` exactly as written (now including `strip_punctuation`,
  S-009's fix to a pre-existing gap — S-006's original implementation
  stripped diacritics but not punctuation).
- The `matchingCandidates.count == 0` and `== 1` branches, matching this
  pseudocode's logic (checked via `Data.PlayerStore`/COMP-06, effective
  data, override-aware — `HasEffectiveAttributeAsync`, see ADR-0015).

Deliberately narrower than the pseudocode above, per `MVP-SCOPE.md`'s Tier
0 scoping (not a bug to fix):

- `candidates = ...` is a single exact-match lookup against
  `Player.NormalizedFullName` only — no `PlayerNameIndex` (doesn't exist),
  no `PlayerAlias` union, and no `fuzzy_search` fallback at all. A guess
  that doesn't exactly-normalize-match `Player.FullName` is incorrect,
  full stop — there is no "candidates is empty → fuzzy search" step.
- The `matchingCandidates.count == 0 AND candidates.count == 1` REQ-211
  live-lookup block as written above (gated on a `PlayerNameIndex` match)
  does not exist — `PlayerNameIndex` isn't built. **As of ADR-0018 (S-011
  follow-up), a differently-triggered, Tier-0-simplified version of the
  same idea does exist:** whenever `matchingCandidates` is empty after the
  normal check, `GridGameModule.ScoreSubmissionAsync` re-runs this cell's
  own Wikidata intersection query directly — calling
  `IWikidataLookupService.LookupAndPersistAsync` for a Country×Club cell
  or, as of S-030, `LookupAndPersistClubClubAsync` for a Club×Club cell
  (both are the same calls `GenerateInstanceAsync` already makes for that
  pairing, not a new source; dispatched from one shared
  `LookupLiveMatchesAsync` helper used by both generation- and guess-time
  code, so the two can't drift on which pairings are handled) — then
  re-checks `matchingCandidates` once. This is unconditional on candidate
  count and has no `ExternalApiUsage`/API-Football fallback leg — Wikidata
  alone isn't a scarce resource (ADR-0011), so there is no budget to gate
  on. The refresh only knows how to resolve a Country×Club or Club×Club
  cell (the two pairings `GenerateInstanceAsync` can produce); any other
  pairing, or a category value no longer present in
  `CountryDefinition`/`ClubDefinition`, skips the refresh and falls through
  to incorrect rather than throwing. See ADR-0018 for why this is judged
  safe without the `PlayerNameIndex` prerequisite in Tier 0.
- `if matchingCandidates.count > 1: → return disambiguation prompt` is
  replaced entirely: Tier 0 auto-accepts the lowest-`Id` candidate (the
  same deterministic pick REQ-204's future uniqueness grouping depends on)
  and logs a warning instead, per REQ-209's status note. There is no
  disambiguation prompt, no `isDisambiguationResolution` parameter, and no
  extra round-trip — a multi-candidate guess is scored and its attempt
  consumed in the same request as any other guess.

**Grid generation (REQ-101, REQ-102, REQ-103)**

```
for each cell (row, col) in NxN:
    attempts = 0
    repeat:
        // REQ-109/ADR-0012: pick from the curated reference tables, never
        // ad hoc from whatever's already in PlayerAttribute
        candidateRow, candidateCol = pick random rows from
            CountryDefinition / ClubDefinition / TrophyDefinition
            (whichever category types this GridTemplate allows)
        if candidateRow.type == "country" AND candidateCol.type == "country":
            continue  // REQ-107: Country×Country is never generated —
                      // checked before the data query, not after
        matchCount = query PlayerAttribute where matches(candidateRow) AND matches(candidateCol)
        if matchCount == 0:
            matchCount = live_lookup(candidateRow, candidateCol)   // REQ-103, ADR-0011 waterfall
            if found: persist as PlayerData(unverified)
        attempts++
    until matchCount >= MIN_VALID_ANSWERS or attempts > MAX_ATTEMPTS
    if attempts exceeded: abort generation, log error, alert admin
```

**Tier 0 status (S-007):** the pseudocode above is the full/long-term
shape (independent per-cell retry, any category type on either axis,
alerting an admin on abort). `GridGameModule.GenerateInstanceAsync`
(`XGArcade.Games.XGGrid`) currently implements a narrower, structurally
different-but-equivalent algorithm: row headers are N unique countries
picked once up front (never retried individually — REQ-107's ban can
never fire on a country picked alone), then column headers are picked one
club at a time from the shuffled candidate pool and accepted only once
validated against *every already-fixed row header* in one pass (all N
match-counts computed together, not cell-by-cell) — a rejected club
candidate is discarded and never revisited, and `attempts` counts
column-candidates tried, not individual cell retries. Both shapes satisfy
REQ-101/102's actual acceptance criteria (all N×N cells valid, N unique
row/column headers, abort after `MaxAttempts` or `MaxDuration` with a
logged error) with the same `MinValidAnswers`/`MaxAttempts` defaults (5 /
500, `GridGenerationOptions`) plus, as of ADR-0023, a `MaxDuration`
wall-clock deadline (default 90s) checked alongside `MaxAttempts` on every
loop iteration — added after a real run chained enough live-lookup misses
to run for 4+ minutes before an infrastructure ingress killed it, since
`MaxAttempts` alone never bounds wall-clock time in practice (the
reference-data pool is far smaller than 500). "Alert admin" is not
implemented — abort currently only logs (`ILogger.LogError`) and returns a
500 with a problem-details body, from either `POST /internal/grid/generate`
or (as of S-008) `POST /internal/generate-round`
(`XGArcade.Api.Rounds.InternalRoundEndpoints`, which catches
`GridGenerationException` — and, as of the 2026-07-12 fix, any other
exception too — and surfaces it the same way), no separate alerting
channel exists yet.
This shape is also Tier 0-scoped to two possible pairings, chosen once per
instance by `SelectPairing` (`GridGameModule.GenerateInstanceAsync`):
Country (rows) × Club (columns), or, as of S-030, Club × Club — never a
mixed axis within one grid, never Trophy — so the "whichever category
types this GridTemplate allows" line above still doesn't vary *within* a
single grid, only across grids.

**REQ-110 (S-036):** `PlayerCacheWarmingService` (`XGArcade.Games.XGGrid`)
iterates every Country × Club and Club × Club pair the reference tables can
produce, triggering the same live-lookup path `GetMatchCountAsync` uses
during real generation for any pair not already at `MinValidAnswers`. It's
invoked via a second `dotnet run --` CLI verb in `Program.cs`
(`warm-player-cache`, same shape as the existing `migrate-and-seed` verb),
run by its own `warm-player-cache.yml` workflow — deliberately not an HTTP
endpoint, and deliberately not a fire-and-forget background task inside
the deployed app: this job can take a long time (every reference pair, up
to a real ~15-27s live Wikidata call each per ADR-0011), which would hit
the same ~240s ingress wall ADR-0023 fixed round generation against if run
synchronously inside a request, and this Container App's `minReplicas: 0`
scale-to-zero (NOTES.md, 2026-07-09) would silently drop a background
task's progress on a scale-down mid-run. A plain foreground CI-runner
process, bounded only by the workflow's own job timeout, has neither
problem.

**REQ-109 correction/recovery path (S-037):** `ReferenceDataSeeder.SeedAsync`
(`XGArcade.Data.Seeding`) is no longer purely additive — a `Countries`/
`Clubs` entry whose `Name` already exists in the database but whose
`WikidataQid` in code has changed now updates that row's `WikidataQid` in
place, rather than being skipped as already-seeded. This matters because a
wrong QID here is otherwise undetectable by construction: `WikidataClient`'s
SPARQL queries have no way to know a QID is the wrong entity, so a wrong
QID that happens to also be a real Wikidata item just returns real, but
wrong, player data — exactly what happened for 4 of S-036's club entries
(Napoli, AS Roma, Sevilla, Porto), caught only by manual verification
against live Wikidata pages (NOTES.md, 2026-07-13). Correcting the QID in
code and re-running `migrate-and-seed` is not enough on its own, though:
whatever was already fetched and persisted under the old, wrong QID
(`PlayerData`/`PlayerAttribute` rows for that club) stays in place and
stays wrong, since nothing in that persisted data distinguishes an old,
wrong-QID row from a correct one.

`StaleClubAttributeCleaner` (`XGArcade.Data.Seeding`) is the recovery tool
for that gap: a third `dotnet run --` CLI verb, `clean-stale-club-attributes
"<comma-separated club names>"` (club names as one comma-separated argument
so a name containing a space, e.g. "AS Roma", survives a GitHub Actions
`workflow_dispatch` text input intact), run by its own
`clean-stale-club-attributes.yml` workflow. It deletes every `PlayerData`
and `PlayerAttribute` row of type `club` whose value matches one of the
given names, querying `XGArcadeDbContext` directly rather than through
`IPlayerStoreRepository` — acceptable here because this code lives inside
`XGArcade.Data` itself (COMP-06), the same direct-DbContext precedent
`ReferenceDataSeeder` already sets for reference tables, not an external
caller reaching around COMP-06's interface. Unlike `migrate-and-seed`'s
other backfillers (`PlayerNormalizedFullNameBackfiller`,
`UserDisplayNameBackfiller`, `LeagueMembershipBackfiller`), this is
deliberately **not** wired into `migrate-and-seed`'s automatic,
safe-to-run-forever chain: it has no way to tell an old wrong-QID row from
a freshly correct one, so it must be triggered manually, once, for the
specific club name(s) just corrected — and strictly *before* the next
`warm-player-cache` run, since running it after a fresh warm would delete
the new, correct data too.

**All-clubs mode (2026-07-17, REQ-111):** the verb also accepts the exact
literal `--all-clubs` in place of a name list
(`StaleClubAttributeCleaner.CleanAllSeededClubsAsync`), added for the
REQ-113 truthy-`wdt:P54` query-shape incident, where the cached club data
of **every** seeded club was incomplete at once — and hand-typing ~32
club names is exactly the typo surface where one misspelled name silently
stays stale (the named mode can't distinguish a typo from a club with
nothing to clean; both remove zero rows and report success). It resolves
the club-name list from the `ClubDefinition` reference table at runtime
and then runs the same per-name cleanup — scoped by the reference table
exactly as the named form is scoped by its list, never "every `club`-type
row regardless of value" — and reports the resolved names back so the
operator can verify what was swept. Two fail-loud guards: (1) all-clubs
mode against an empty `ClubDefinition` table **throws** instead of
cleaning nothing (zero seeded clubs means a wrong database or a
never-seeded one, not a genuine "nothing to clean"); (2) in the named
form, any `-`-prefixed token (e.g. a mistyped `--all-club`) **throws**
before any deletion in `Program.cs`'s argument handling, rather than
matching zero rows and printing a plausible "removed 0 rows" success —
no seeded club name starts with `-`, so this never rejects a real list.
Same manual, workflow_dispatch-only friction as the named mode, same
clean-then-warm ordering rule, still never wired into `migrate-and-seed`.

**REQ-112 player pool restriction (S-038, ADR-0025):** the same
"can't selectively fix already-cached data" problem, but for the whole
pool rather than a few named clubs — neither sex nor date of birth was
ever recorded on cached `Player`/`PlayerAttribute` rows, so there is no
way to tell which existing rows would have passed the new male/born-1939-
or-later filters and which wouldn't without a live Wikidata re-check per
player anyway. A fourth CLI verb, `purge-player-pool "delete all player
data"`, deletes every `Player` row (cascading, via `ON DELETE CASCADE`,
through `PlayerData`/`PlayerOverride`/`PlayerAttribute`/`PlayerAlias`) —
gated behind a required, exact confirmation-phrase argument rather than
`clean-stale-club-attributes`'s bare "is this blank" check, since a
bulk, unscoped delete triggered by a single GitHub Actions text input has
a meaningfully larger blast radius than a per-club-name-scoped one (same
`"promote to prod"`-style extra-friction pattern
`infra/scripts/promote-dev-to-prod.sh` already uses for its own
destructive write). Run once via `purge-player-pool.yml`, then a normal
`warm-player-cache.yml` run repopulates the pool entirely under the new
filters. Reference tables (`CountryDefinition`/`ClubDefinition`/
`TrophyDefinition`) and account/game-history tables (`User`/`League`/
`Round`/`GridInstance`/`GridCell`/`Guess`) are untouched —
`Guess.PlayerAnswerId` has no FK constraint on `Player` (see
`XGArcadeDbContext.cs`'s `OnModelCreating`), so an old `Guess` whose
answer was a since-purged player keeps its already-computed
`IsCorrect`/score, it just can no longer display which player that was.

**REQ-214 backfill (S-045):** `Player.PhotoUrl` is only ever set at the
moment a `Player` row is first created
(`WikidataLookupService.GetOrCreatePlayerAsync`, see §6/REQ-214's own note
above) — a row created by an earlier `warm-player-cache` run, before the
`P18` addition shipped, has `PhotoUrl` permanently `NULL` with no other
code path that will ever revisit it. `PlayerPhotoBackfillService`
(`XGArcade.DataSync.Wikidata`, same project as `WikidataLookupService`/
`PlayerNameIndexImporter` — it needs both `IWikidataClient` and
`IPlayerStoreRepository`, and `XGArcade.Data` has no reference back to
`XGArcade.DataSync`, so it can't live in `XGArcade.Data`) closes that gap
without a destructive wipe-and-rerun — a full `purge-player-pool` +
`warm-player-cache` cycle would cascade into `PlayerAttribute`/`Guess`/
`GridCell` history this codebase explicitly protects (REQ-710's
anonymize-never-hard-delete precedent is the same instinct applied to a
different table). It's a sixth `dotnet run --` CLI verb,
`backfill-player-photos`, same shape as every verb above (builds its
dependencies directly, reuses `ConfigureWikidataHttpClient`), run manually
via `backfill-player-photos.yml` (`workflow_dispatch` only) — squarely
inside ADR-0024's existing "CLI verb, never an HTTP endpoint or background
task" decision, not a new one.

Two new members support it:
- `IWikidataClient.QueryPlayerPhotosByQidsAsync` — a batched, direct-by-QID
  lookup, a fundamentally different SPARQL shape from the two intersection
  queries (`SELECT ?player ?photo WHERE { VALUES ?player { wd:Q1 wd:Q2 ... }
  OPTIONAL { ?player wdt:P18 ?photo. } }`, no candidate-matching pattern, no
  male/date-of-birth re-filter — every QID in the batch is already a real
  `Player` row this codebase created). Same throw-on-failure contract as
  `QueryPlayerPoolBirthYearAsync` (`WikidataQueryException` on timeout/HTTP/
  parse failure), per `docs/coding-guidelines.md`'s 2026-07-18
  error-handling guideline: this is a batch job whose success metric is a
  backfilled-row count, so a swallowed failure would be indistinguishable
  from "none of these QIDs have a photo."
- `IPlayerStoreRepository.GetPlayersMissingPhotoAsync`/
  `UpdatePlayerPhotosAsync` — a paged read (`WikidataQid IS NOT NULL AND
  PhotoUrl IS NULL`, bounded by a batch size, never the whole table) and a
  batch write (one `SaveChangesAsync` per batch, load-then-save per
  `docs/coding-guidelines.md`, never `ExecuteUpdateAsync`).

`PlayerPhotoBackfillService.BackfillAsync` loops: fetch up to `BatchSize`
(200 — a conservative batch for the VALUES-clause query, safely inside
§6a's "few-thousand-row, no ORDER BY/LIMIT/OFFSET" bounded-query class)
missing-photo players, query Wikidata for that batch, write back whichever
QIDs resolved to a photo. Each call to `GetPlayersMissingPhotoAsync` also
takes an `excludingPlayerIds` set that the service grows with every player
ID it has already attempted **this run** (success or failure) — needed
because Guid has no LINQ-translatable ordering to keyset-paginate on, and a
plain `Skip`/`Take` can't be used either: a successfully-backfilled batch
removes rows from the query's own `WHERE PhotoUrl IS NULL` filter between
calls, which would silently skip untouched rows on the next page. The
exclusion set is what guarantees the loop terminates instead of re-fetching
a failed batch forever.

Two deliberate, documented judgment calls (both in
`PlayerPhotoBackfillService`'s own doc comment):
- **Per-batch failure handling: log-and-continue, not
  `PlayerNameIndexImporter`'s retry-then-fail-loud.** The two jobs'
  failure-detection problem differs in kind: `PlayerNameIndexImporter`
  needs to fail loudly because an empty year result is genuinely ambiguous
  with a swallowed failure unless it's tracked and reported. Here, a
  failed batch just leaves those players' `PhotoUrl` `NULL`, and the very
  next full re-run's `GetPlayersMissingPhotoAsync` call surfaces them again
  automatically — there is no equivalent ambiguity to fail loudly about,
  so failing the whole run over one transient batch failure would cost an
  operator a manual re-run for no extra signal a log line doesn't already
  give them.
- **Accepted limitation, same class as `PlayerCacheWarmingService`'s own
  "below `MinValidAnswers`, re-queried every run" note:** a player who
  genuinely has no Wikidata `P18` statement stays `PhotoUrl == NULL`
  forever (correctly — nothing to backfill), so every future full re-run
  re-queries Wikidata for that player again. There is no persisted
  "already checked, genuinely has no photo" signal distinct from "never
  checked." Accepted because this job is meant to run occasionally, not on
  a tight recurring schedule.

Note on live lookups in practice: since most external sources are
player/club-centric rather than intersection-queryable, a live lookup for a
missing combination typically means fetching a club's squad history (a
bulk, cacheable call) and filtering locally by nationality/other attributes,
rather than querying the intersection directly. This is naturally covered by
the caching layer described in section 2. See `MVP-SCOPE.md`'s Tier 0
section for the fully worked example (fetch Arsenal's squad once, cache
every nationality found, answer many country combinations from that one call).

**Uniqueness score (REQ-204, REQ-205)**

```
live (on every page load, not persisted permanently until the Round closes):
    // The denominator counts ONLY correct guesses, one per player — never
    // incorrect guesses or burned attempts. This was a real bug in an
    // earlier draft of this pseudocode (review-2026-07-07-design.md,
    // finding 2): counting every Guess including incorrect ones let how
    // much *failing* happened on a cell distort everyone's score, which
    // has nothing to do with answer rarity. See REQ-204's own acceptance
    // criteria and UniquenessCalculator's doc comment for the same rule.
    //
    // ADR-0020 (S-022): the comparison excludes the guesser's own guess
    // from both sides — comparing a guess against itself is degenerate,
    // and a naive self-inclusive version made a lone correct guesser score
    // 0% unique (100% of a population of themselves "sharing" their own
    // answer), the opposite of the intended "first/only correct answer is
    // maximally unique" behavior.
    totalGuesses = COUNT(Guess WHERE CellId = X AND IsCorrect = true)   // includes my own guess
    otherGuesses = totalGuesses - 1
    IF otherGuesses == 0:
        uniqueScore = 1.0   // no other correct guesser yet to compare against
    ELSE:
        sameAnswer       = COUNT(Guess WHERE CellId = X AND IsCorrect = true AND PlayerAnswerId = myAnswer)   // includes my own guess
        othersSameAnswer = sameAnswer - 1
        uniqueScore = 1 - (othersSameAnswer / otherGuesses)

before locking (ADR-0021, S-028): for each user with >=1 Guess in this
Round (a "participant"), find every cell of the Round's grid instance
(via IGameModule.GetCellIdsAsync, never a direct game-table read) that
user has no Guess row for, and INSERT a synthetic one:
    Guess.IsCorrect = false, Guess.PlayerAnswerId = null
    Guess.AttemptCount = 0, Guess.SubmittedName = ""   // distinguishes
                                                        // "never attempted"
                                                        // from a real wrong
                                                        // guess (AttemptCount >= 1)
    // A user with zero Guess rows in this Round at all (never opened it)
    // is not a participant and gets nothing synthesized.

at Round.EndTime (scheduled job):
    for each Guess in Round (including any just synthesized above):
        if IsCorrect: compute uniqueScore as above (now against final data)
        Guess.FinalUniquenessScore = uniqueScore if IsCorrect else null
        // ADR-0021: xG Arcade is scored like golf — LOWER is better, and
        // the goal is to MINIMIZE total points, not maximize them. A
        // correct guess's points are therefore the *inverse* of
        // uniqueScore (rarer answer -> fewer points -> better), and an
        // incorrect/unattempted guess scores the WORST case, not 0 (0 is
        // now the *best* possible score, so it must never be free).
        Guess.FinalPoints = round((1 - uniqueScore) * MAX_POINTS_PER_CELL) if IsCorrect else MAX_POINTS_PER_CELL
    persist
```

`MAX_POINTS_PER_CELL` resolves to `100` (`ScoringRules.MaxPointsPerCell`,
`XGArcade.Core.Scoring`) — no document specified an exact value for "how
many points is the worst-case (fully common, incorrect, or unanswered)
outcome worth"; this is the Tier 0 default, chosen and recorded here
(S-011), same non-appsettings-bound, plain-constant pattern as
`GuessRules.MaxAttemptsPerCell`. The `round((1 - uniqueScore) *
MAX_POINTS_PER_CELL)` computation itself is written in exactly one place,
`ScoringRules.PointsFromUniqueScore(double uniqueScore)` (extracted S-018,
inverted S-028/ADR-0021) — both the "at Round.EndTime" `FinalPoints`
locking above and a live, provisional `LivePoints` estimate returned by
`GET /rounds/current` for any correctly-guessed, still-open cell (REQ-204
extension) call this same method, never two independently-written copies
of the formula.

Race conditions (REQ-603) are handled by keeping `Guess` inserts simple
(insert/update, no incremental counter to keep in sync) — the calculation is
always done via a `COUNT()` query against current table data, which is
atomic at the database level.

**Tier 0 status (S-008/S-009/S-011, extended S-018, corrected S-022):** as of
S-011, both halves of this pseudocode are implemented, in
`XGArcade.Core.Scoring`: `UniquenessCalculator.Calculate` is the "live"
half, called both by `GET /rounds/current`
(`XGArcade.Api.Rounds.RoundEndpoints`, on every request, never persisted)
and by `IScoreLockingService`/`ScoreLockingService`'s "at Round.EndTime"
half, which `RoundCloseService` (`XGArcade.Core.Rounds`) now calls at round
close — the same formula is shared, so the live value and the final locked
value can never disagree by construction. As of S-018, the `uniqueScore →
points` half of the formula was likewise consolidated into
`ScoringRules.PointsFromUniqueScore`, so `GET /rounds/current`'s new live
`LivePoints` field and `ScoreLockingService`'s `FinalPoints` locking share
that code too, not just the uniqueness calculation upstream of it. As of
S-022 (ADR-0020), `UniquenessCalculator.Calculate` excludes the guesser's
own guess from both sides of the ratio (see the pseudocode above) — a lone
correct guesser has `uniqueScore = 1.0`. As of S-028 (ADR-0021, xG Arcade
scored like golf — lowest total wins), that now locks/live-estimates at
`FinalPoints`/`LivePoints = 0` (the *best* score), not `MaxPointsPerCell`;
an incorrect guess locks at `FinalPoints = MaxPointsPerCell` (the *worst*
score, was `0`); and `ScoreLockingService.MaterializeUnansweredCellsAsync`
(new in S-028) synthesizes a `MaxPointsPerCell`-scored `Guess` row, before
locking, for each cell a round participant never attempted — resolved via
the new `IGameModule.GetCellIdsAsync`, implemented in `GridGameModule` by
reading the already-generated `GridInstance`'s `Cells`.
`RoundCloseService` itself still only pulls a round's `EndTime` forward
(idempotently — never later than what's already scheduled) before
delegating. **Correction (S-029/ADR-0022):** this used to be invoked only
via REQ-806's non-Production `POST /internal/test-data/force-close-round/{roundId}`,
with no automated scheduled job calling round-close anywhere — that gap is
now closed. `RoundGenerationService.GenerateNextRoundIfNeededAsync` (the
one piece of code `generate-round.yml`'s cron actually invokes) now also
closes the round it is about to supersede — never `latest` itself, but its
predecessor, found via the new `IRoundRepository.GetPreviousByGameKeyAsync`
— before deciding whether to generate a new one. See ADR-0022 for the full
derivation and the accepted trade-offs (a small backlog of never-closed
rounds from before this fix needs one extra cron cycle each to catch up;
the pre-existing, documented concurrent-call race in
`MaterializeUnansweredCellsAsync` is now reachable from this real scheduled
path too, still not fixed). The non-Production force-close-round endpoint
still exists, unchanged, for manual/E2E use.

**Leaderboard pagination (REQ-607)**

```
GET /leagues/{leagueId}/leaderboard?cursor={lastSeenRank}&pageSize=50
    → query LeagueMembership JOIN aggregated Guess.FinalPoints
      WHERE LeagueId = leagueId
      ORDER BY totalPoints ASC   // ADR-0021: lowest total wins
      OFFSET/cursor-based pagination, LIMIT pageSize
    → response includes the requesting user's own rank/row even if it falls
      outside the current page (SCREEN-03's sticky "your position" footer
      needs this without a second round-trip)
```

Cursor-based (not raw offset) pagination is preferred once league sizes grow
large enough for offset pagination's performance to degrade; for MVP scale,
a simple offset is acceptable but the API contract should already look
cursor-shaped so switching the implementation later doesn't change callers.

**Built as (S-034):** matches the pseudocode's contract shape, with two
MVP-scale implementation choices called out below. `GET
/leagues/global/leaderboard` (`XGArcade.Api.Leagues.LeaderboardEndpoints`
→ `ILeaderboardService`/`LeaderboardService`, `XGArcade.Core.Leagues`)
takes optional `cursor`/`pageSize` query params — `pageSize` defaults to
50, capped at a `MaxPageSize` of 100 (a request above the cap or `<= 0` is
a 400, not a silent clamp); `cursor` defaults to 0 and is the last-seen
global rank, rejected with a 400 if negative, but an out-of-range-but-valid
cursor (e.g. stale, from a since-shrunk league) is not an error — it falls
back to an empty final page. The response is `LeaderboardResponse { Rows,
RequestingUserRow, NextCursor, HasMore }`; each `Rows` entry carries an
explicit, global 1-based `Rank` (not a page-local array index — a later
page no longer starts at rank 1), and `RequestingUserRow` is always
populated with the caller's own row even when it falls outside the current
page, so SCREEN-03's sticky "your position" footer never needs a second
round-trip. Still the global league only (no `{leagueId}` route parameter
— custom leagues don't exist yet, deferred per `MVP-SCOPE.md`/T-109).

Two deliberate MVP-scale choices, not gaps: (1) member `DisplayName`s and
each member's `SUM(Guess.FinalPoints ?? 0)` are still fetched via
`GuessRepository.GetTotalFinalPointsByUserIdsAsync`'s database-side
`GROUP BY` for the *full* membership, but ranking and the `cursor`/
`pageSize` slice itself happen in memory afterward — bounding the
*response*, not doing a DB-level `ORDER BY`/`LIMIT`; acceptable at Tier 0
membership sizes, revisit if the global league's membership grows large
enough for this to matter. (2) pagination is a plain in-memory offset
under a cursor-shaped contract (per this section's original note above),
so switching to a real keyset/cursor implementation later doesn't change
callers. Frontend (`LeaderboardScreen.tsx`): a "Load more" button appends
subsequent pages on top of what's already loaded; a pinned "you" footer
renders `requestingUserRow` when it isn't already present among the loaded
rows; the existing 15s poll re-fetches only page 1, with a dedup fix so a
player whose rank crosses the page-1/page-2 boundary between polls isn't
shown twice.

**Account deletion — anonymize, don't hard-delete Guess rows (REQ-710)**

```
on account deletion request (after confirmation):
    UPDATE Guess SET UserId = NULL WHERE UserId = deletedUserId
    -- leaderboard totals, other players' uniqueness percentages, and
    -- round history all remain accurate — they never depended on *whose*
    -- guess it was, only on the count and the answer
    DELETE FROM NotificationPreference WHERE UserId = deletedUserId
    DELETE FROM LeagueMembership WHERE UserId = deletedUserId
    DELETE FROM User WHERE Id = deletedUserId
    call Supabase Auth admin API to delete the underlying identity
```

No background job is needed for this at MVP scale — it's a single
transaction. Revisit only if the volume of related rows ever makes this
slow enough to need async processing.

**Built as (S-025):** matches this pseudocode with one scoping note and one
deliberate ordering choice. `DELETE FROM NotificationPreference` is a no-op
in Tier 0 — that table doesn't exist yet (notification preferences are
Tier 1, `MVP-SCOPE.md`) — not a step silently skipped, just nothing to do
yet. The Supabase Auth identity deletion is genuinely the *last* step and a
separate, non-transactional HTTP call (not folded into "a single
transaction" the way the local `Guess`/`LeagueMembership`/`User` writes
are) — `AccountDeletionService` (`XGArcade.Core.Auth`) surfaces a failure
here to its caller rather than swallowing it, but there's no retry/saga if
it fails after the local writes already committed; see ADR-0026's
consequences for the accepted trade-off. `IUserRepository`/`IGuessRepository`/
`ILeagueRepository`'s new methods (`DeleteAsync`, `AnonymizeByUserIdAsync`,
`RemoveMembershipsByUserIdAsync`) all load-then-`SaveChangesAsync` through
the EF Core change tracker rather than `ExecuteDeleteAsync`/
`ExecuteUpdateAsync` — this codebase's tests run against EF Core's InMemory
provider (§7), which doesn't support translating those bulk operations.

## 6a. External API shapes (reference)

Worth knowing before implementing `DataSync.Clients` (COMP-07): these three
external APIs are not uniformly shaped, and `DataSync.Clients` needs
genuinely different client implementations behind one shared interface,
not one generic HTTP client reused three times.

**API-Football** — plain REST, the easy case:

```
GET https://v3.football.api-sports.io/players?id=276
Header: x-apisports-key: {API_KEY}
```

Every response is a consistent envelope: `{ get, parameters, errors,
results, response }`. Rate-limit state comes back as response headers on
every call (`x-ratelimit-requests-remaining`, etc.) — useful for
`ExternalApiUsage` reconciliation if the local counter and the provider's
own count ever drift. GET-only, single auth header, no OAuth/token
refresh to manage.

**Resend** — same shape as API-Football: REST, JSON, single API key header.

**Wikidata** — a fundamentally different paradigm, not a REST resource
API: a SPARQL graph query sent to one endpoint
(`https://query.wikidata.org/sparql?query={SPARQL}`). Requires knowing
Wikidata's property/entity ID vocabulary (e.g. `P106` = occupation,
`P27` = country of citizenship, `P54` = member of sports team, `P21` = sex
or gender, `P569` = date of birth, `Q937857` = "association football
player", `Q6581097` = male). The `Q142`/`Q9617`-style IDs below come from
`CountryDefinition`/`ClubDefinition`'s `WikidataQid` field (ADR-0012) —
never hardcoded, never resolved fresh per query. A query answering this
system's actual intersection use case (REQ-101 — "who satisfies both row
and column categories") looks like:

```sparql
SELECT ?player ?playerLabel WHERE {
  ?player wdt:P106 wd:Q937857.   # occupation: association football player
  ?player wdt:P27 wd:Q142.       # country of citizenship: e.g. France
  ?player p:P54 ?clubStatement.  # member of sports team: full statement path,
  ?clubStatement ps:P54 wd:Q9617.       # e.g. Arsenal F.C. — NOT truthy wdt:P54
  MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. } # (REQ-113, rule 4 below)
  ?player wdt:P21 wd:Q6581097.   # sex or gender: male (REQ-112, ADR-0025)
  ?player wdt:P569 ?dateOfBirth. # date of birth (REQ-112, ADR-0025)
  FILTER(?dateOfBirth >= "1939-01-01T00:00:00Z"^^xsd:dateTime) # fixed cutoff, not rolling
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
}
```

(The Club × Club variant checks P54 twice, once per club, through **two
distinct statement variables** — a single shared `?clubStatement` could
never bind, since one P54 statement can't point at two clubs.)

The response shape is SPARQL's own JSON results format (`head.vars` /
`results.bindings`), not a simple object list — needs its own parsing
logic, distinct from the two REST clients above. Response times are
variable under current WDQS load; always call with a timeout (15s, per
ADR-0011's 2026-07-09 addendum — chosen over the ADR's original "e.g.
5-10s" illustrative range because the ADR's own evidence shows WDQS queries
taking 9-27s under load) and treat a timeout the same as a miss, falling through to
API-Football.

Four rules that make this query correct, not just functional:

- **Never `LIMIT` the intersection query.** Its results ARE the cell's
  answer key: fetching *all* matches is exactly what makes Tier 0's
  cache-only guess-checking complete and fair without needing guess-time
  live verification (REQ-211, Tier 1). Adding a LIMIT for "performance"
  silently reintroduces the correct-guess-marked-wrong bug REQ-211 exists
  to fix. Result sets here are small (rarely >100 players) — there is no
  performance problem to solve.
- **Fetch `skos:altLabel` in the same query** and store the aliases as
  `PlayerAlias` rows. Wikidata already curates nicknames/alternate names
  ("Pelé" ↔ "Edson Arantes do Nascimento"); one extra SELECT column gives
  plain-text guess matching most of REQ-208's alias value for free, with
  no manual alias curation and no separate Tier 1 system needed.
- **Upsert players by `WikidataQid`**, never insert per query — see the
  `Player` entity's dedup note in §5.
- **Always filter to male (`P21`) and date of birth on or after
  1939-01-01 (`P569`)** (REQ-112, ADR-0025) — a fixed literal on
  `WikidataClient`, not a rolling window computed from the current time
  (an earlier draft used a `TimeProvider`-driven rolling "latest 100
  years" cutoff; the user corrected this to a fixed date, which also
  removed the need for any clock dependency here). Neither property is
  recorded on the persisted `Player`/`PlayerAttribute` rows — the filter
  exists only at query time, so a player already outside these bounds is
  simply never fetched in the first place, never fetched-then-excluded.
- **P54 must use the full statement path (`p:P54`/`ps:P54`), excluding
  only `wikibase:DeprecatedRank` via `MINUS` — never the truthy `wdt:P54`
  shortcut** (REQ-113). Wikidata's truthy `wdt:` graph contains only
  best-rank statements: the moment any P54 statement on a player is
  marked preferred rank (editors routinely mark the *current* club
  preferred), every normal-rank historical club silently vanishes from
  `wdt:P54` — turning "ever played for this club" into "currently plays
  for this club" for exactly those players. This shipped as a real bug
  (e.g. Sandro Tonali × AC Milan scored incorrect) and left the persisted
  answer key incomplete for every seeded club at once; recovery required
  `clean-stale-club-attributes --all-clubs` plus a fresh
  `warm-player-cache` pass (see §6's CLI-verb notes and REQ-111). The
  other properties (`P106`/`P27`/`P21`/`P569`) stay truthy on purpose —
  for those, best-rank semantics match product intent. Deprecated rank is
  still excluded: it's Wikidata's "recorded but wrong" marker, not a
  historical spell.
- **`P18` (image) is fetched `OPTIONAL`, same shape as `alias`** (REQ-214,
  S-043, backend half) — added to both intersection query builders
  (`BuildCountryClubIntersectionQuery`/`BuildClubClubIntersectionQuery`),
  never the birth-year bulk-import query below (that one deliberately
  still has no `P18`, unchanged from the 2026-07-18
  `RemovePlayerNameIndexPhotoUrl` decision — see that migration's note a
  few paragraphs down). `wdt:P18` is a `commonsMedia`-typed property, so
  Wikidata's SPARQL endpoint resolves it directly to a fully-qualified
  Special:FilePath URL, not an entity QID — unlike `?player`, there is no
  `/entity/Qnnn` suffix to split off; the raw binding value is the usable
  photo URL. Parsed into `WikidataPlayerMatch.PhotoUrl` (nullable, "first
  non-null seen" per QID, same as the alias/nationality grouping elsewhere
  in this file) and persisted once at player creation into a new
  `Player.PhotoUrl` column (`AddPlayerPhotoUrl` migration) — never a
  `PlayerAttribute` row; see Player's own doc comment in §5 for why. This
  shape could not be verified against a live Wikidata query when built (no
  network access in that environment) — flagged for manual verification,
  same precedent as prior QID/property additions (S-036/S-037).

**S-032 addition, revised 2026-07-18 — `PlayerNameIndex`'s bulk import
slices by birth year, never by `LIMIT`/`OFFSET`.** `WikidataClient
.QueryPlayerPoolBirthYearAsync` (COMP-10/ADR-0007's bulk name-index
refresh, run by `PlayerNameIndexImporter`) queries the same
`P106`=`Q937857` occupation broadly with the same male-only filter, but
with no country/club filter at all — a far larger pool (hundreds of
thousands of items) than any intersection query's "rarely >100 players."
S-032's original shape paged it with an inner
`SELECT DISTINCT ?player ... ORDER BY ?player LIMIT 5000 OFFSET n`
subquery; in production that `ORDER BY` forced WDQS to materialize and
sort the entire unfiltered pool on *every* page request, which hit WDQS's
hard ~60s **server-side** timeout on every single page (no client-side
timeout can raise it), and the then-swallow-to-`[]` error contract made
the importer read the first timed-out page as end-of-data — every run
exited 0 having imported nothing (see NOTES.md 2026-07-18). The replacement
is the standard WDQS pattern for large pools: one bounded query per birth
year (`?dateOfBirth >= "{year}-01-01" && < "{year+1}-01-01"`,
1939 → current year), each a few-thousand-row query with **no `ORDER BY`,
`LIMIT`, `OFFSET`, or subquery** — the same size/shape class as the
intersection queries that already work. Two paired contract changes,
deliberate and load-bearing:
- `QueryPlayerPoolBirthYearAsync` **throws** (`WikidataQueryException`) on
  timeout/HTTP/parse failure instead of returning `[]` — an empty list
  means exactly "no eligible players born this year" (real for sparse
  early years). The intersection queries' never-throw contract is
  untouched (REQ-103 depends on it).
- `PlayerNameIndexImporter` retries a failed slice (3 attempts, short
  backoff), finishes the remaining years (each successful slice is
  upserted immediately), then **fails the whole run loudly** if any slice
  failed — S-032's "partial import is an accepted trade-off" is reversed;
  the job is idempotent and manually re-run, so a red workflow beats a
  silent exit 0.

Deliberately does not fetch `P54` (club) — that data belongs to
`PlayerAttribute`, not this index (ADR-0007) — but does fetch `P569`
(birth year, via `BIND(YEAR(?dateOfBirth) AS ?birthYear)`) and `P27`
(nationality, display-only). `P18` (photo) was dropped in the 2026-07-18
revision along with the `PhotoUrl` column (`RemovePlayerNameIndexPhotoUrl`
migration): the autocomplete contract never exposed a photo
(design-document.md's SCREEN-02 note), so it was pure query/storage cost.
A player with more than one citizenship produces more than one result row
for the same `?player`; the client groups these client-side, keeping the
first non-null value seen, same shape as how alias rows are grouped for
the intersection queries above. A player with two `P569` statements in
different years appears in two slices — the importer's deterministic
QID-derived `PlayerId` upsert collapses that into one row.

Semantics note: the Country category means **citizenship (P27)**, not
"capped for the national team" — a deliberate, player-visible rule
(dual citizens match both countries; a naturalized player matches their
citizenship even without caps). If a player disputes a cell, this is the
rule to point at.

**Tier 0 uses United Kingdom, not England, specifically to avoid the
exception below** (see `MVP-SCOPE.md` for the reasoning) — this makes
`P27` uniform across every country in the current list, no special case
needed in `DataSync.Clients` yet.

**Known limitation for Tier 1's future "national teams" feature:** none
of England, Scotland, Wales, or Northern Ireland are sovereign states, so
`P27` citizenship for their players is uniformly `Q145` (United Kingdom),
never the home nation specifically — querying `P27 = Q21` (England)
directly returns nothing. The property that actually means "which country
represented in competition" is **`P1532`** ("country for sport") —
Wikidata's own definition matches exactly what football fandom means by
"England." When national teams are added (a distinct Tier 1 feature, not
just swapping United Kingdom back to England), this needs a second query
path in `DataSync.Clients` alongside the existing `P27` one — citizenship
and "country represented in competition" are genuinely different concepts
for dual nationals and naturalized players, so this is correct modeling,
not incidental complexity to simplify away.

Semantics note: the Club category means **senior/first-team career
only** — a deliberate decision, not the "any club *entity* a P54
statement points at" default. This is about which club QIDs count
(REQ-109's resolution rule), not about statement ranks — it does not
contradict the full-statement-path rule above, which deliberately
accepts *every* non-deprecated P54 statement against the senior club's
QID (REQ-113's "ever played for" semantics). `ClubDefinition.WikidataQid`
must point at the senior club's specific item, not a generic club-family
concept, so that a youth academy with its own distinct Wikidata item
(common for well-documented clubs) is naturally excluded — the youth
spell links to the youth item, not this one. **This is not guaranteed for every club/player**: a thin or
poorly-maintained Wikidata page can record a youth-only appearance
directly against the senior club's own QID with nothing distinguishing
it. No secondary filter is planned to catch this in Tier 0 (an
"appearances" qualifier is sometimes present on P54 statements but too
inconsistently populated to rely on) — if a youth-only player slips
through as a valid answer and it's noticed, correct it via the existing
manual override (S-012), the same mechanism already built for this class
of gap. Don't build speculative additional filtering before real data
shows how often this actually happens.

**Supabase (data)** — not accessed as a REST API from the backend for
normal data access; it's a standard Postgres connection string used
through EF Core/Npgsql like any other Postgres database. Supabase's own
REST/GraphQL layer (PostgREST) isn't part of this system's design — see
`implementation-document.md` §1 for why direct Postgres access was chosen.

**Supabase Auth (identity), added S-004** — unlike the data path above,
this one genuinely is called as a REST API from the backend: `SupabaseAuthClient`
(`XGArcade.Core/Auth/`) calls `POST {Supabase:Url}/auth/v1/signup` and
`POST {Supabase:Url}/auth/v1/token?grant_type=password`, both with an
`apikey`/`Authorization: Bearer` header set to `Supabase:AnonKey` (a
publishable client key by Supabase's own design, not a secret in the same
sense as the database connection string or JWT signing secret). This is
the backend-mediated signup/login decision — see ADR-0013 — rather than
the frontend calling Supabase Auth's JS client directly.

**Supabase Auth admin call (identity deletion), added S-025** —
`SupabaseAuthClient.DeleteUserAsync` calls `DELETE {Supabase:Url}/auth/v1/admin/users/{id}`,
the one call site in this system using `Supabase:ServiceRoleKey` instead of
`Supabase:AnonKey` (set as a per-request header override, not on the shared
`HttpClient`, so the anon key configured as this client's default is never
sent on this specific request) — see ADR-0026 for why this needed a new,
genuinely privileged secret rather than reusing the anon key above.

## 7. Testing strategy

| Level | Tool | What is tested | Example |
|---|---|---|---|
| Unit (backend) | NUnit | Pure business logic without DB/network | Grid validation rules, scoring calculation, override merge |
| Unit (frontend) | Vitest | Component logic, formatting | "12%" formatting, live-badge state |
| API | NUnit + `WebApplicationFactory<Program>` + Testcontainers (Postgres) | Endpoints end-to-end against a real (containerized) DB | POST /guesses, GET /leaderboard |
| UI/E2E | Playwright | Full flows in the browser | Guess → see live % → round closes (mocked time) → see locked score |
| Data sync | NUnit with a mocked HTTP client | Sync respects overrides | The REQ-501 scenario explicitly |

**Example NUnit test names (for traceability to requirements):**

```csharp
[Test]
public void REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers() { }

[Test]
public void REQ501_Sync_DoesNotOverwrite_ExistingPlayerOverride() { }

[Test]
public void REQ204_UniquenessScore_RecalculatesOnEachPageLoad() { }
```

This naming convention makes it easy to link test coverage directly to
requirement IDs, and reuses the pattern from SpecOps (spec-to-test
traceability).

**Playwright – example test flow (REQ-204/205):**

```typescript
test("shows live badge and locked score correctly", async ({ page }) => {
  await page.goto("/round/active");
  await page.getByTestId("cell-0-0").fill("Henry");
  await page.getByTestId("cell-0-0").press("Enter");

  const liveBadge = page.getByTestId("unique-live-badge");
  await expect(liveBadge).toBeVisible();
  await expect(liveBadge).toContainText("LIVE");

  // simulate round closing via test API/seed
  await page.request.post("/test-utils/close-round");
  await page.reload();

  await expect(page.getByTestId("unique-final-badge")).toBeVisible();
  await expect(page.getByTestId("unique-live-badge")).toHaveCount(0);
});
```

## 8. CI/CD, infrastructure as code, and operations (free-tier framework)

See ADR-0004 for hosting/IaC rationale and ADR-0006 for the environment
split and sync approach.

**Environments** (ADR-0006):

- **Dev**: its own Container App, Static Web App, and Supabase
  project (the second of the free plan's two). `ASPNETCORE_ENVIRONMENT` is
  set to e.g. `Testing`, which registers the `/internal/test-data/*`
  endpoints (COMP-09). CI's Playwright/API test runs, and local dev by
  default, point here.
- **Production**: as before — `ASPNETCORE_ENVIRONMENT=Production`, which
  never registers the test-data endpoints regardless of any other config.

**Infrastructure as code** (`/infra/bicep`):

- Composed as modules, not one flat template: `container-apps-environment.bicep`,
  `backend-container-app.bicep`, `static-web-app.bicep`, orchestrated by `main.bicep`
- Parameters (region, resource names, container image tag) live in
  `main.parameters.json`; no secrets in these files — secrets (Supabase
  connection string, JWT signing config) are passed as GitHub Actions
  secrets into Container App secret references at deploy time
- The same modules deploy both environments via separate parameter files
  (`main.parameters.json` for prod, `main.parameters.dev.json` for
  dev) — different `appName`/`environmentTag`, same templates
- Applying the templates is idempotent (`az deployment group create`), so
  re-running a deploy after a manual portal change reconciles state back to
  what's defined in Bicep

**Game-data sync, bidirectional** (`/infra/scripts/sync-prod-to-dev.sh` and
`/infra/scripts/promote-dev-to-prod.sh`, REQ-804/REQ-805, ADR-0009):

- Both scripts source one shared allowlist
  (`/infra/scripts/lib/game-data-tables.sh`: Player, PlayerData,
  PlayerOverride, PlayerAttribute, PlayerNameIndex, PlayerAlias,
  TrophyDefinition, ClubCrest, GridTemplate) so the two directions can't
  drift apart on what's safe to move — anything not on the allowlist is
  never touched, which is what keeps `User`/`NotificationPreference`/
  `League`/`LeagueMembership`/`Guess`/`Round`/`GridInstance`/`GridCell`/Auth
  tables out by construction, not by a filter that could be forgotten.
  Note `GridInstance`/`GridCell` are explicitly never included — they're
  specific to actual generated rounds, inherently per-environment
- **`promote-dev-to-prod.sh`** is the recommended day-to-day direction:
  curate game data in dev, ship it to prod
- **`sync-prod-to-dev.sh`** is the fallback direction, for when prod's
  game data changed directly and dev needs to catch up
- Both triggered manually via `workflow_dispatch`
  (`promote-dev-to-prod.yml` / `sync-prod-to-dev.yml`), never on a
  schedule — deliberate actions, not routine ones. The prod-writing
  direction requires a longer confirmation phrase as extra friction

**CI/CD** (`/.github/workflows`):

- **`ci.yml`**: Tier 0 shape — NUnit + Vitest, then Playwright E2E against
  a local stack composed inside the workflow (Postgres service container +
  API from source), needing no cloud dev environment or test-data API.
  Tier 1 shape: adds a `deploy-dev` job (dev-tagged image + Bicep deploy)
  that E2E depends on, with the test-data reset call (REQ-802) — restored
  when the dev environment exists (ADR-0006)
- **`sync-players.yml`**: scheduled (e.g. daily), runs `XGArcade.DataSync`
  against production, respects overrides (REQ-501)
- **`generate-round.yml`**: scheduled per the configured frequency
  (REQ-301), calls a backend endpoint to create a new Round
- **`deploy.yml`**: on push to `main` — builds and pushes the backend image
  to GHCR, deploys the Bicep templates to production, and deploys the
  frontend build to Azure Static Web Apps. This is effectively a promotion
  step (the commit already passed CI and deployed cleanly to dev); it
  builds its own image tag separately from `ci.yml`'s dev build, an
  accepted duplication for now given project scale
- **`promote-dev-to-prod.yml`**: manual-only, the recommended game-data
  promotion direction
- **`sync-prod-to-dev.yml`**: manual-only, the fallback game-data sync direction
- **`backup-database.yml`**: scheduled daily — Supabase's free tier includes
  no automated backups at all (confirmed directly against their docs,
  2026-07-05), so this is not optional. Runs `pg_dump` against production
  and uploads the result as a workflow artifact with a bounded retention
  window (14 days is a reasonable starting point — balances real recovery
  usefulness against GitHub's own artifact storage quota, which is a
  separate free allowance worth watching). A restore procedure using
  `pg_restore` must be documented in `infra/README.md` and tested manually
  at least once — an untested backup is not a backup (REQ-901).

**Failure alerting (REQ-902):** GitHub's own email notifications for failed
workflow runs cover this at zero additional cost and zero additional
infrastructure — but only if enabled. This is a one-time account setting
to confirm, not something to build: check GitHub notification settings
(Settings → Notifications → Actions) so failed-workflow emails actually
reach an inbox someone checks, and do a deliberate test (break a workflow
on purpose once) to confirm the notification arrives before relying on it.

**Cost thresholds to watch:** Container Apps Consumption plan free grant
(180k vCPU-seconds / 2M requests per month) — now shared across two
environments' usage; Supabase database size (free ~500MB per project, and
dev counts against the free plan's 2-project limit); and the number of
API calls against external data sources. As usage grows: cache
aggressively, consider premium features as discussed earlier.

**Keeping framework versions current** (`.github/dependabot.yml`): minor
and patch updates for NuGet, npm, GitHub Actions, and the backend's Docker
base image are grouped and proposed weekly, so the specific versions
verified in this document (§1) don't silently drift out of date. Major
version bumps (e.g. a future .NET 10 → 12 move) are deliberately not
auto-grouped — they need a human decision and a version bump in this
document's §1 table, not a routine merge.

## 9. Suggested implementation order

1. Core domain + data model + migrations
2. PlayerData/PlayerOverride/PlayerAttribute + merge logic (unit-tested)
3. Grid generation + validation (unit-tested)
4. Guess endpoint + uniqueness calculation (API-tested)
5. Round scheduler + locking at close
6. Frontend: grid UI + live badge
7. Leagues (global + custom) + leaderboard
8. Admin review view for unverified data
9. End-to-end tests across the full flow
10. Data sync jobs against Wikidata/API-Football

## 10. Open technical questions

- Container Apps Consumption plan scale-to-zero cold-start impact on
  scheduled jobs (`sync-players.yml`, `generate-round.yml`) — may need a
  minimum-replica setting if cold starts cause missed schedules
- Whether Testcontainers is practical in the CI environment, or whether
  SQLite in-memory is sufficient for API tests early on
- Rate-limiting strategy against external data sources during live lookups (REQ-103)
- Sending domain/subdomain setup for Resend (e.g. a dedicated
  `auth.<domain>` vs `notifications.<domain>` split, per Resend/Supabase's
  own guidance to separate auth and marketing/product sending reputation)
  — depends on whatever real domain replaces the "xG Arcade" placeholder
