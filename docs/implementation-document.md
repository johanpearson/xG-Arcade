---
doc_id: implementation-document
title: Implementation Document
version: "0.31"
status: draft
last_updated: 2026-07-10
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

Version 0.31 · 2026-07-10
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

**Tier 0 status (S-004):** only HTTPS redirection, CORS, and JWT validation
are actually wired in `Program.cs` today, in that order
(`UseHttpsRedirection` → `UseCors("Frontend")` → `UseAuthentication` →
`UseAuthorization` → `MapControllers`). Rate limiting and admin
authorization are not yet implemented — no admin endpoints exist yet
(`Admin__UserIds`-based authorization is S-012's job, per `docs/backlog.md`)
and no story has wired ASP.NET Core's rate limiting middleware yet, so the
pipeline above remains the full/long-term target, not current behavior for
those two steps.

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
    /auth                        -> AuthScreen (login/signup, REQ-701)
    /grid                        -> GridScreen, Grid, GridCell, CellState,
                                     GuessInput, CategoryLabel (SCREEN-01/01a/02)
    /leaderboard                 -> LeaderboardScreen (SCREEN-03, REQ-401/404's
                                     Tier 0 slice — added S-011, global league only)
    /lib                          -> api.ts (typed fetch client), types.ts,
                                     categoryDisplay.ts, guessRules.ts
  /tests
    /unit                       -> Vitest — only the pre-S-010 App/health-check
                                   test remains here; newer component tests
                                   live under /src (see above)
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
public class PlayerNameIndex
{
    public Guid PlayerId { get; set; }              // same id space as PlayerAttribute's PlayerId
    public string PrimaryName { get; set; }
    public string NormalizedName { get; set; }      // lowercased, diacritics stripped — see REQ-208
    public int? BirthYear { get; set; }             // disambiguation display only
    public string PrimaryNationality { get; set; }  // disambiguation display only
    public string PhotoUrl { get; set; }            // optional, nullable
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
    // fixes one axis to Country and the other to Club (MVP-SCOPE.md), so
    // within a single grid these are redundant with
    // GridTemplate.AllowedCategoryTypes today — but recording them per cell
    // is what lets guess-checking (S-009) know whether to query
    // PlayerAttribute's "nationality" or "club" AttributeType for a given
    // cell without re-deriving it, and keeps the schema correct once a
    // future Tier 1 grid mixes category types across an axis (REQ-107
    // already allows Club x Club).
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
| `PlayerNameIndex` | `(NormalizedName)` | Not built (Tier 1, no `PlayerNameIndex` table exists yet) — recorded here as the long-term index once autocomplete/COMP-10 exist. Every guess submission normalizes and looks up against this first (REQ-208) |
| `PlayerAlias` | `(NormalizedAlias)` | Alias lookup on the fallback path when the primary name doesn't match (REQ-208) |
| `ExternalApiUsage` | `(Source, Date)` unique | Checked on every guess-time live-lookup candidacy check (REQ-211); must be fast since it's in the hot guess-submission path |
| `CountryDefinition` / `ClubDefinition` / `TrophyDefinition` | `(Name)` unique | Grid generation picks from these directly (REQ-109); uniqueness also prevents an admin accidentally adding the same club twice under slightly different casing |
| `User` | `(AuthProviderUserId)` unique | Every authenticated request resolves this first |

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

**Tier 0 status (S-009):** the pseudocode above is the full/long-term
shape. What `GuessSubmissionService` (`XGArcade.Core.Scoring`) +
`GridGameModule.ScoreSubmissionAsync` (`XGArcade.Games.XGGrid`) actually
implement, real and tested:

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
  live-lookup block does not exist. A single name-matching candidate with
  no cached `PlayerAttribute`/`PlayerOverride` data for the cell's
  categories is simply excluded from `matchingCandidates` — never
  triggers `live_lookup`.
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
row/column headers, abort after `MaxAttempts` with a logged error) with
the same `MinValidAnswers`/`MaxAttempts` defaults (3 / 500,
`GridGenerationOptions`); "alert admin" is not implemented — abort
currently only logs (`ILogger.LogError`) and returns a 500, from either
`POST /internal/grid/generate` or (as of S-008) `POST /internal/generate-round`
(`XGArcade.Api.Rounds.InternalRoundEndpoints`, which catches the same
`GridGenerationException` and surfaces it the same way), no separate
alerting channel exists yet.
This shape is also Tier 0-scoped to Country (rows) × Club (columns) only —
never a mixed axis, never Trophy — so the "whichever category types this
GridTemplate allows" line above doesn't yet vary in practice.

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
    totalGuesses = COUNT(Guess WHERE CellId = X AND IsCorrect = true)
    sameAnswer   = COUNT(Guess WHERE CellId = X AND IsCorrect = true AND PlayerAnswerId = myAnswer)
    uniqueScore  = 1 - (sameAnswer / totalGuesses)

at Round.EndTime (scheduled job):
    for each Guess in Round:
        if IsCorrect: compute uniqueScore as above (now against final data)
        Guess.FinalUniquenessScore = uniqueScore if IsCorrect else null
        Guess.FinalPoints = round(uniqueScore * MAX_POINTS_PER_CELL) if IsCorrect else 0
    persist
```

`MAX_POINTS_PER_CELL` resolves to `100` (`ScoringRules.MaxPointsPerCell`,
`XGArcade.Core.Scoring`) — no document specified an exact value for "how
many points is a 100%-unique correct guess worth"; this is the Tier 0
default, chosen and recorded here (S-011), same non-appsettings-bound,
plain-constant pattern as `GuessRules.MaxAttemptsPerCell`.

Race conditions (REQ-603) are handled by keeping `Guess` inserts simple
(insert/update, no incremental counter to keep in sync) — the calculation is
always done via a `COUNT()` query against current table data, which is
atomic at the database level.

**Tier 0 status (S-008/S-009/S-011):** as of S-011, both halves of this
pseudocode are implemented, in `XGArcade.Core.Scoring`:
`UniquenessCalculator.Calculate` is the "live" half, called both by `GET
/rounds/current` (`XGArcade.Api.Rounds.RoundEndpoints`, on every request,
never persisted) and by `IScoreLockingService`/`ScoreLockingService`'s "at
Round.EndTime" half, which `RoundCloseService` (`XGArcade.Core.Rounds`)
now calls at round close — the same formula is shared, so the live value
and the final locked value can never disagree by construction.
`RoundCloseService` itself still only pulls a round's `EndTime` forward
(idempotently — never later than what's already scheduled) before
delegating; that trigger remains invoked today only via REQ-806's
non-Production `POST /internal/test-data/force-close-round/{roundId}` —
there is still no automated scheduled job that calls round-close at a
round's real `end_time` in any environment.

**Leaderboard pagination (REQ-607)**

```
GET /leagues/{leagueId}/leaderboard?cursor={lastSeenRank}&pageSize=50
    → query LeagueMembership JOIN aggregated Guess.FinalPoints
      WHERE LeagueId = leagueId
      ORDER BY totalPoints DESC
      OFFSET/cursor-based pagination, LIMIT pageSize
    → response includes the requesting user's own rank/row even if it falls
      outside the current page (SCREEN-03's sticky "your position" footer
      needs this without a second round-trip)
```

Cursor-based (not raw offset) pagination is preferred once league sizes grow
large enough for offset pagination's performance to degrade; for MVP scale,
a simple offset is acceptable but the API contract should already look
cursor-shaped so switching the implementation later doesn't change callers.

**Tier 0 status (S-011):** the pseudocode above is the full/long-term
shape; it is not built yet. What's actually built: `GET
/leagues/global/leaderboard` (`XGArcade.Api.Leagues.LeaderboardEndpoints`
→ `ILeaderboardService`/`LeaderboardService`, `XGArcade.Core.Leagues`)
implements the aggregation itself (member `DisplayName`s joined with each
member's `SUM(Guess.FinalPoints ?? 0)`, computed database-side via
`GuessRepository.GetTotalFinalPointsByUserIdsAsync`'s `GROUP BY`, sorted
descending), but for the global league only (no `{leagueId}` route
parameter — custom leagues don't exist yet) and with no pagination at all:
the endpoint returns every member's row in one response, unbounded. This
is a real, deliberately-acknowledged gap against REQ-607's own pagination
clause, not a Tier-0-scoped-out item — see REQ-607's status note in
`requirements-document.md` for the explicit trigger to revisit.

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
`P27` = country of citizenship, `P54` = member of sports team, `Q937857`
= "association football player"). The `Q142`/`Q9617`-style IDs below
come from `CountryDefinition`/`ClubDefinition`'s `WikidataQid` field
(ADR-0012) — never hardcoded, never resolved fresh per query. A query
answering this system's actual intersection use case (REQ-101 — "who
satisfies both row and column categories") looks like:

```sparql
SELECT ?player ?playerLabel WHERE {
  ?player wdt:P106 wd:Q937857.   # occupation: association football player
  ?player wdt:P27 wd:Q142.       # country of citizenship: e.g. France
  ?player wdt:P54 wd:Q9617.      # member of sports team: e.g. Arsenal F.C.
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
}
```

The response shape is SPARQL's own JSON results format (`head.vars` /
`results.bindings`), not a simple object list — needs its own parsing
logic, distinct from the two REST clients above. Response times are
variable under current WDQS load; always call with a timeout (15s, per
ADR-0011's 2026-07-09 addendum — chosen over the ADR's original "e.g.
5-10s" illustrative range because the ADR's own evidence shows WDQS queries
taking 9-27s under load) and treat a timeout the same as a miss, falling through to
API-Football.

Three rules that make this query correct, not just functional:

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
only** — a deliberate decision, not the "any P54 statement" default.
`ClubDefinition.WikidataQid` must point at the senior club's specific
item, not a generic club-family concept, so that a youth academy with its
own distinct Wikidata item (common for well-documented clubs) is
naturally excluded — the youth spell links to the youth item, not this
one. **This is not guaranteed for every club/player**: a thin or
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
