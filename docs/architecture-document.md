---
doc_id: architecture-document
title: Architecture Document
version: "0.15"
status: draft
last_updated: 2026-07-09
owner: Johan
related_docs:
  - requirements-document.md
  - implementation-document.md
id_prefix: ARCH
read_before: ["requirements-document.md"]
read_after: []
update_when:
  - "A container or component is added, removed, or its responsibility changes"
  - "A data flow between components changes"
  - "A cross-cutting concern (auth, logging, config) changes approach"
  - "An architecture decision is made — also add an ADR under docs/decisions/"
---

# Architecture Document – xG Arcade (working title)

Version 0.15 · 2026-07-09
References: `requirements-document.md`, `implementation-document.md`

> **Naming note:** "xG Arcade" is a placeholder for the overall product name.
> xG Grid is the first game hosted on it, not the platform itself — see
> `requirements-document.md` §0 for the full distinction. Every reference to
> a game-specific concept in this document (e.g. `GridInstance`) is scoped to
> xG Grid and must not leak into `Core.*` components.

> **For AI agents:** this document defines WHY the system is structured the
> way it is — component boundaries, responsibilities, and data flow. Read
> this before `implementation-document.md` when the task involves adding a
> new component, changing a boundary, or understanding how pieces fit
> together. If your change affects a boundary described here, update this
> document and add an ADR under `docs/decisions/`. Component IDs (`COMP-xxx`)
> are stable identifiers — reference them in code comments and commit
> messages where relevant.
>
> **This document describes the full system, not what's being built right
> now.** See `MVP-SCOPE.md` (repo root) for the actual build order — several
> components below (COMP-07's Wikidata client, COMP-10, the dev/prod split)
> are Tier 1, not needed to get a first playable version working.

## 1. Purpose and audience

This document describes the structural design of the platform: its major
components, their responsibilities, how they communicate, and the
architectural decisions behind them. It is the reference for anyone (human
or AI agent) who needs to understand where a piece of logic belongs before
writing code, not just how to write the code itself (that's
`implementation-document.md`).

## 2. Architectural style

- **Modular monolith**, not microservices. One deployable backend, internally
  divided into modules with clear boundaries (`Core`, game modules, `Data`,
  `DataSync`). Rationale: team size is one developer, operational overhead of
  microservices is not justified at this stage, and module boundaries can be
  extracted into services later if ever needed without a rewrite.
- **Pluggable game modules**: the platform's core (users, leagues, rounds,
  scoring) is game-agnostic. Each game (starting with the grid game)
  implements a shared `IGameModule` interface. This is the mechanism that
  satisfies the "platform for multiple games" requirement without
  over-engineering the first game.
- **Cache-first data strategy**: no bulk upfront data import; the player
  attribute store grows on demand (see `implementation-document.md` §2 and
  ADR-0001).

## 3. System context (C4 Level 1)

```
                     ┌───────────────┐
                     │     Player     │
                     └───────┬────────┘
                             │ plays, guesses, views leaderboards
                             ▼
                  ┌────────────────────┐
   ┌─────────────▶│    The xG Arcade     │◀─────────────┐
   │  reviews/     │  (hosts xG Grid  │   configures  │
   │  corrects data│   and future games) │   templates/  │
   │               └──┬───────┬───────┬──┘   schedules   │
   │                  │       │       │                   │
┌──┴───┐  live lookups│       │       │ auth + confirmation/  scheduled sync
│ Admin │             ▼       │       │ notification emails    ▼
└───────┘   ┌─────────────────┐ │     ▼                ┌───────────────┐
            │ External data   │ │  ┌─────────────┐     │  Scheduler    │
            │ sources (Wiki-  │ │  │ Email        │     │  (GitHub      │
            │ data, API-Foot- │ │  │ provider     │     │  Actions cron)│
            │ ball)           │ │  │ (Resend)     │     └───────────────┘
            └─────────────────┘ │  └─────────────┘
                                 ▼
                        ┌─────────────────┐
                        │ Auth provider    │
                        │ (Supabase Auth)  │
                        └─────────────────┘
```

**Actors and external systems:**

| Name | Type | Role |
|---|---|---|
| Player | Person | Plays rounds, submits guesses, views leaderboards |
| Admin | Person | Reviews unverified data, configures templates and round schedules |
| External data sources | External system | Wikidata, API-Football — source of player attribute data |
| Scheduler | External system | GitHub Actions cron — triggers round generation and sync jobs |
| Auth provider | External system | Supabase Auth — identity, session management, and account confirmation state |
| Email provider | External system | Resend — sends auth emails (via Supabase custom SMTP) and product notification emails (via direct API from Core.Notifications) — see ADR-0005 |

## 4. Containers (C4 Level 2)

| ID | Container | Responsibility | Tech |
|---|---|---|---|
| CONT-01 | Web Frontend | Renders grid, guess input, leaderboards, admin review UI | TypeScript / React, hosted on Azure Static Web Apps |
| CONT-02 | Backend API | Business logic, request handling, scoring, orchestration | C# / ASP.NET Core, containerized, hosted on Azure Container Apps |
| CONT-03 | Database | Persists users, leagues, rounds, guesses, player data, overrides | PostgreSQL (Supabase); Supabase Auth also used for identity |
| CONT-04 | Sync Worker | Scheduled job that refreshes player data from external sources | C# console job, containerized, triggered by GitHub Actions |
| CONT-05 | Round Scheduler Job | Scheduled job that generates new Round + game-specific instance (e.g. a GridInstance for xG Grid) | C# console job / API endpoint, triggered by GitHub Actions |

Data flow between containers is always frontend → backend API → database; no
container other than the Backend API writes to the database directly, so
business rules (e.g. override precedence) are enforced in one place.

## 5. Components (C4 Level 3) — inside the Backend API

| ID | Component | Responsibility | Maps to (implementation doc) |
|---|---|---|---|
| COMP-01 | Core.Users | User accounts, auth integration | `XGArcade.Core` |
| COMP-02 | Core.Leagues | Global + custom leagues, membership | `XGArcade.Core` |
| COMP-03 | Core.Rounds | Round lifecycle, scheduling config | `XGArcade.Core` |
| COMP-04 | Core.Scoring | Uniqueness calculation, score locking | `XGArcade.Core` |
| COMP-05 | Games.XGGrid | Grid generation, category logic, `IGameModule` implementation for the xG Grid game | `XGArcade.Games.XGGrid` |
| COMP-06 | Data.PlayerStore | PlayerData, PlayerOverride, PlayerAttribute; override-merge logic | `XGArcade.Data` |
| COMP-07 | DataSync.Clients | Wikidata/API-Football clients, live-lookup fallback | `XGArcade.DataSync` |
| COMP-08 | Core.Notifications | Sends product notification emails (round results) via Resend's API; owns notification preferences. Does not handle auth emails — those are Supabase Auth's responsibility, configured with custom SMTP. See ADR-0005 | `XGArcade.Core` |
| COMP-09 | Testing.SeedManager | Test-data creation/reset/scenario API. Registered only when the environment is not Production — see ADR-0006 | `XGArcade.Api` (conditionally registered), reaches other components' normal write paths, never a separate data path |
| COMP-10 | Data.PlayerNameIndex | Broad, bulk-imported name/alias index used only for autocomplete and as the candidate pool for name matching (REQ-207/208/209). Deliberately separate from COMP-06's narrow, incrementally-built validation cache — see ADR-0007 | `XGArcade.Data` |

**Boundary rule 1 (data access):** COMP-05 (and any future game module) may
only reach player data through COMP-06's public interface. It must never
query `PlayerData`/`PlayerOverride` directly — this keeps the
override-precedence rule (REQ-501) enforced in exactly one place. If a new
game module needs a different kind of data store, that's a signal for an
ADR, not a workaround.

**Boundary rule 2 (Round genericity):** `Core.Rounds` (COMP-03) must never
hold a foreign key to a game-specific entity such as `GridInstance`. A
`Round` references a game instance only via an opaque pair —
`GameKey` (e.g. `"xg-grid"`) and `GameInstanceId` (a `Guid` with no
type Core understands). Resolving that ID into an actual `GridInstance` is
the responsibility of the owning game module (COMP-05), reached through
`IGameModule`. This is what makes it possible to add a second game later
without changing `Core.Rounds` at all — see ADR-0003.

**Boundary rule 3 (email separation):** Auth-lifecycle emails (signup
confirmation, password reset) are never sent by `XGArcade.Core` code — they
are Supabase Auth's responsibility, configured with custom SMTP. Conversely,
product notification emails (round results) are never routed through
Supabase Auth or an auth hook — they are sent directly by Core.Notifications
(COMP-08) via Resend's API. See ADR-0005 for why these stay separate.

**Boundary rule 4 (test-data isolation):** Testing.SeedManager (COMP-09)
must create and reset data only by calling other components' normal
public interfaces (e.g. Core.Rounds' round-creation logic, Core.Leagues'
league-creation logic) — never by writing directly to tables through a
separate path. This guarantees test data is always structurally valid
exactly like real data, and that a business-rule change only needs to be
implemented once. See ADR-0006.

**Boundary rule 5 (autocomplete/correctness separation):** Autocomplete and
name matching (REQ-207/208/209) query only `Data.PlayerNameIndex` (COMP-10).
Correctness-checking a submitted guess (REQ-203) queries only
`Data.PlayerStore` (COMP-06), never COMP-10. These two paths must never be
merged — doing so would leak answer validity through autocomplete. See ADR-0007.

## 6. Key data flows

**6.1 Grid generation flow** (realizes REQ-101, REQ-102, REQ-103, REQ-109)

**Explicit rule, not just implied by the diagram below:** every live
lookup this round's cells will ever need happens *during generation*,
before `Round` (the thing players can actually see/play) is created at
all. There is no "player guesses, we fetch on demand" path at grid-gen
time — a `Round` only exists once every cell already has a validated,
cached answer. This is what makes the "local DB only, no guess-time
Wikidata fallback" answer-checking strategy (REQ-211 deferred to Tier 1)
defensible: by the time anyone can play, the data is already there.

```
Round Scheduler Job (COMP-03)
  → Games.XGGrid (COMP-05): "generate instance for template X"
    → Data.PlayerStore (COMP-06): pick candidate row/column values from
      CountryDefinition/ClubDefinition/TrophyDefinition (ADR-0012) —
      never derived ad hoc from whatever's already in PlayerAttribute
    → Data.PlayerStore: query candidate combinations
      → [miss] DataSync.Clients (COMP-07): live lookup — Wikidata first
        (timeout-bounded, using the category values' resolved WikidataQid;
        skipped entirely if either value's QID is still null), API-Football
        only as fallback if Wikidata doesn't resolve it (ADR-0011);
        API-Football calls count against the shared daily counter (ExternalApiUsage)
        → Data.PlayerStore: persist as unverified
    → Games.XGGrid: assemble GridInstance once all cells valid, return its ID
  → Core.Rounds (COMP-03): create Round with GameKey="xg-grid",
    GameInstanceId=<the returned ID> — Core never sees the GridInstance shape
```

**6.1a Club addition and external ID resolution** (realizes REQ-109, ADR-0012)

```
[admin-triggered, one time per new club — not per grid, not per guess]
Admin → Web Frontend (admin view) → Backend API: add new ClubDefinition
  → DataSync.Clients (COMP-07): resolve Wikidata QID (entity search) and
    API-Football team ID (team search), best-effort
  → Data.PlayerStore: persist ClubDefinition with whatever was resolved —
    a still-null QID or team ID is a valid state, not an error (REQ-109);
    the live-lookup waterfall degrades gracefully around it
```

**6.2 Guess submission and scoring flow** (realizes REQ-201–REQ-206, REQ-207–REQ-211)

```
Player → Web Frontend: types a guess
  → Data.PlayerNameIndex (COMP-10): autocomplete suggestions — a broad
    pool, never sourced from COMP-06 (REQ-207)
Player → Web Frontend → Backend API: POST guess (selected/typed name)
  → Core.Rounds: validate round is active, guess-change policy;
    resolve GameKey to find the owning game module (ADR-0003)
  → Games.XGGrid (COMP-05): reject immediately if this cell is already
    correct, or if 2 attempts are already used (REQ-210) — checked before
    any name resolution work, not after
  → Games.XGGrid: resolve the name to a candidate player
    → normalize + alias + fuzzy match against Data.PlayerNameIndex (REQ-208)
    → if multiple candidates match the name, check each against the cell's
      categories via Data.PlayerStore (COMP-06): one match → accept it;
      more than one → return a disambiguation prompt (REQ-209, doesn't
      consume an attempt until resolved); none → incorrect
    → single-candidate case: check against Data.PlayerStore (effective
      data, override-aware)
      → if Data.PlayerStore has NO record at all for this player against
        these category types: DataSync.Clients (COMP-07) performs a live
        lookup — Wikidata first (timeout-bounded), API-Football only as a
        fallback if Wikidata doesn't resolve it (ADR-0011) — checking the
        shared API-Football daily counter only on that fallback path
        → resolved (either source): result persisted immediately as
          unverified PlayerData (REQ-211) — same request, not deferred
        → unresolved (Wikidata failed AND API-Football budget exhausted or
          also unresolved): fail closed, evaluate as incorrect using only
          existing cached data
  → correctness shown to the player immediately (REQ-203) — if correct,
    the cell locks now, regardless of the round's remaining time
  → Core.Scoring: compute live uniqueness on read, not on write
  → Database: persist Guess (AttemptCount incremented, IsCorrect set)

[scheduled, at Round.EndTime]
Round Scheduler Job → Core.Scoring: lock final scores for all guesses in round
  → Database: persist FinalUniquenessScore / FinalPoints
```

**6.3 Data sync flow** (realizes REQ-501, REQ-502, REQ-503)

```
Sync Worker (CONT-04) → DataSync.Clients (COMP-07): fetch updates
  → Data.PlayerStore (COMP-06): write to PlayerData (never PlayerOverride)
  → [merge on read] effective value = PlayerOverride if present, else PlayerData
Admin → Web Frontend (admin view) → Backend API: approve/correct unverified data
  → Data.PlayerStore: create PlayerOverride or mark PlayerData verified
```

**6.4 Signup and email confirmation flow** (realizes REQ-701–REQ-705)

```
Player → Web Frontend → Backend API: POST create account
  → Auth provider (Supabase Auth): create unconfirmed identity
  → Auth provider → Email provider (Resend, via custom SMTP): send
    confirmation email containing both a link and a numeric code
  → Core.Users (COMP-01): create/link local profile record, unconfirmed

Player clicks link OR enters code → Auth provider: verify → mark confirmed
  → Core.Users: reflect confirmed state
[REQ-702] Core.Rounds/Core.Leagues reject actions from unconfirmed accounts
  by checking this state before accepting a guess or league action
```

**6.5 Round-result notification flow** (realizes REQ-706 — Deferred/Phase 2)

```
[scheduled, at Round.EndTime, after Core.Scoring locks final scores]
Round Scheduler Job → Core.Notifications (COMP-08): "notify participants of round X"
  → Core.Notifications: filter to opted-in participants
  → Email provider (Resend, direct API call — not via Supabase Auth):
    send per-player round-result summary email
```

**6.6 Test-data reset flow** (realizes REQ-801, REQ-802, REQ-803 — dev only)

```
[dev environment only; endpoint doesn't exist in prod — ADR-0006]
Test runner / developer → Backend API: POST /internal/test-data/reset
  → Testing.SeedManager (COMP-09): tear down test-created rounds/guesses/
    leagues/synthetic users
  → Testing.SeedManager: recreate baseline via Core.Rounds/Core.Leagues/etc.
    normal creation paths (boundary rule 4) — never a direct table write
```

**6.7 Game-data sync flows, bidirectional** (realizes REQ-804/REQ-805 — ADR-0009)

Both directions share one allowlist (`infra/scripts/lib/game-data-tables.sh`):
`Player`, `PlayerData`, `PlayerOverride`, `PlayerAttribute`,
`PlayerNameIndex`, `PlayerAlias`, `TrophyDefinition`, `ClubCrest`,
`GridTemplate` — game/reference content only. `GridInstance`/`GridCell`
were never included (an earlier version of this doc incorrectly implied
they were — corrected here): they're specific to actual generated rounds,
which are inherently per-environment and never meaningful to sync.

```
Recommended direction — promote-dev-to-prod.sh (REQ-805):
[manual only, never scheduled]
Promotion job → Dev database: read the game-data allowlist
  → Production database: write/merge the same tables
  → User, NotificationPreference, League, LeagueMembership, Guess, Round,
    GridInstance/GridCell, and all Supabase Auth tables are excluded by
    construction — the shared allowlist never includes them

Fallback direction — sync-prod-to-dev.sh (REQ-804):
[manual only, never scheduled]
Sync job → Production database: read the same game-data allowlist
  → Dev database: write/merge the same tables
  → Same exclusions as above, same shared allowlist file
```

**6.8 Account deletion flow** (realizes REQ-710)

```
User → Web Frontend → Backend API: DELETE /account (with confirmation)
  → Core.Users (COMP-01): anonymize all Guess rows belonging to this user
    (sever the UserId link — do not delete the rows, since other players'
    uniqueness scores and leaderboard history depend on the total guess count)
  → Core.Users: delete NotificationPreference, User record
  → Auth provider (Supabase Auth): delete the credential/identity
  → Email becomes available for a new registration
```

**6.9 Backup flow** (realizes REQ-901 — Supabase's free tier has no built-in backups)

```
[scheduled, daily — backup-database.yml]
GitHub Actions → Production database: pg_dump (full export)
  → Store as a workflow artifact (or equivalent off-platform storage),
    with a bounded retention window, separate from the primary database
    and from Supabase entirely — see infra/README.md for the retention
    policy and restore procedure
```

## 7. Cross-cutting concerns

| Concern | Approach |
|---|---|
| Authentication | Delegated to Supabase Auth; backend validates JWTs on every request, does not manage passwords (see ADR-0004) |
| CORS | Restricted to the known frontend origin(s) only, configured via environment variable, never a wildcard — enforced first in the middleware pipeline (before authorization), so an unrecognized origin is rejected regardless of any other check. No configured origin means the policy allows nothing rather than falling back to permissive (REQ-606; see `implementation-document.md` §3 for the full pipeline ordering) |
| Authorization | Two roles at this stage: Player, Admin. Enforced at the API controller level via a policy/attribute, verified by an automated test per admin endpoint (REQ-606) |
| Input validation | All user-supplied input is validated server-side (model validation / explicit checks), regardless of any client-side validation in the frontend (REQ-606) |
| Rate limiting | Applied to sign-up, login, and confirmation-resend endpoints specifically, since these are the abuse-prone surface (REQ-606, REQ-704's resend cooldown) |
| Transport security | HTTPS/TLS everywhere — frontend↔backend, backend↔database, backend↔external providers; no plaintext transport (REQ-606) |
| Secrets management | Environment variables / platform secret stores (GitHub Actions secrets, Container App secrets) for connection strings, API keys; never committed to source control |
| Configuration | Non-secret configuration (cron expressions, feature flags, environment tag) via environment variables, distinct from the secrets store above |
| Dependency security | Automated vulnerability scanning in CI for both backend (NuGet) and frontend (npm) dependencies; a high/critical finding blocks merge (REQ-606) |
| Query performance | Indexed lookups for hot paths — especially `Guess` queries used by uniqueness calculation (REQ-203/204) — and pagination on any list endpoint that can grow unbounded, e.g. leaderboards (REQ-607) |
| Logging | Structured logging in the Backend API; generation failures (REQ-101 abort case) must log with enough context to reproduce |
| Error handling | API returns problem-details style errors; frontend distinguishes user-facing validation errors from system errors |
| Observability | Minimal at MVP stage: logs + free-tier hosting metrics. Revisit if usage grows |
| Test data isolation | A test-data API exists only outside Production, and creates/resets data only through normal component write paths (ADR-0006, boundary rule 4) |
| Backups | Independent daily backup of production, since the hosting free tier includes none — see ADR/REQ-901 and `infra/README.md` |
| Failure alerting | Scheduled jobs (round generation, sync, backups) must surface failures to the operator, not fail silently — REQ-902 |
| Data provider compliance | Terms of service for each external data source are read before relying on it, not assumed — see ADR-0008 |
| Shared external API budget | Live lookups try Wikidata first (not meaningfully capped for this system's volume), falling back to API-Football only when Wikidata can't resolve it. Grid generation (REQ-103) and guess-time verification (REQ-211) share a tracked daily counter for the API-Football fallback specifically — see ADR-0011, which corrected an earlier design (ADR-0010) that mistakenly treated API-Football as the only source |
| Account data rights | Deletion anonymizes rather than hard-deletes `Guess` rows, preserving other users' historical scores while removing the personal link — REQ-710/711 |

## 8. Quality attribute drivers

| Attribute | Driver | Architectural response |
|---|---|---|
| Testability | REQ-601 | Modular monolith with clear component boundaries; business logic has no direct DB/network dependency (see implementation doc test strategy) |
| Cost | REQ-602 | Modular monolith avoids per-service hosting cost; cache-first data strategy avoids storage/API-call cost growth |
| Extensibility | xG Arcade vision (multiple games) | `IGameModule` boundary isolates game-specific logic from Core |
| Data integrity | REQ-501 | Single write path to PlayerData/PlayerOverride via COMP-06; override precedence enforced in one place |
| Consistency of correctness | REQ-203 | Answer-checking always uses locally cached effective data, not a live external call, so mid-round changes to external sources can't shift correctness |

## 9. Deployment view

| Environment | Frontend | Backend | Database/Auth | Notes |
|---|---|---|---|---|
| Local | Vite dev server | `dotnet run` or local container | Points at the dev Supabase project | Docker Compose optional for a fully local Postgres instead |
| Dev | Azure Static Web Apps (Free tier), separate app | Azure Container Apps (Consumption plan), separate app, `ASPNETCORE_ENVIRONMENT != Production` | Supabase project #2 (of the free plan's 2) | Used by CI's automated tests, manual QA, and local dev; hosts the test-data API (COMP-09) — see ADR-0006 |
| Production | Azure Static Web Apps (Free tier) | Azure Container Apps (Consumption plan), image from GHCR | Supabase project #1 (Postgres + Auth) | GitHub Actions builds/pushes the image and applies Bicep; see ADR-0004 |

Dev exists specifically to satisfy REQ-801–804 (testability) at
zero additional cost — it uses the second of Supabase's two free projects
and a second, equally free, Container Apps/Static Web Apps deployment
(Consumption/Free tiers are billed by usage, not by environment count).
This replaces the earlier "no separate staging environment" position now
that a concrete testability need justifies it — see ADR-0006.

Dev redeploys automatically on every PR and push to `main` (via `ci.yml`'s
`deploy-dev` job, which E2E tests depend on), so it never drifts from
current code the way a manually-updated environment would. Prod deploys
on every push to `main` via `deploy.yml` — effectively a promotion step
once a commit has already passed CI on dev.

IaC lives under `/infra/bicep` as composed modules (Container Apps
environment, Container App, Static Web App), not one flat template — see
`implementation-document.md` §8 and ADR-0004 for the full rationale. The
same modules are reused for both environments via a per-environment
parameters file (`main.parameters.json` vs `main.parameters.dev.json`).

## 10. Architecture Decision Records

Significant decisions are recorded as individual ADRs under
`docs/decisions/`, using the template in `docs/decisions/0000-template.md`.
Do not edit historical ADRs to reflect new decisions — supersede them with a
new ADR that references the old one.

| ADR | Title | Status |
|---|---|---|
| ADR-0001 | Incremental data cache instead of upfront database import | Accepted |
| ADR-0002 | Modular monolith instead of microservices | Accepted |
| ADR-0003 | Round references game instances generically, never a game-specific FK | Accepted |
| ADR-0004 | Hosting on Azure Container Apps + Static Web Apps, Bicep for IaC, Supabase for data/auth | Accepted |
| ADR-0005 | Custom SMTP via Resend for auth emails; separate Notifications component for product emails | Accepted |
| ADR-0006 | Two-project environment split, gated test-data API, one-way non-PII sync | Accepted |
| ADR-0007 | A broad player name index for autocomplete, separate from the narrow validated attribute cache | Accepted |
| ADR-0008 | Data provider terms-of-service compliance approach | Accepted (one pre-launch action item) |
| ADR-0009 | Bidirectional game-data sync (dev↔prod), never results or customer data | Accepted (supersedes ADR-0006's one-way clause) |
| ADR-0010 | Live verification at guess time, sharing the API budget with grid generation | Accepted (budget model superseded by ADR-0011) |
| ADR-0011 | Wikidata-first waterfall for live lookups; API-Football as fallback only | Accepted |
| ADR-0012 | Category value reference tables, each with resolved external IDs (Wikidata QID / API-Football team ID) | Accepted |
| ADR-0013 | Backend-mediated signup/login (proxying Supabase Auth's REST API), not frontend-direct | Accepted |

## 11. Glossary

See `requirements-document.md` §2 for domain terms (Grid, Cell, Round, Guess,
Uniqueness score, Override, Unverified data). This document additionally
uses:

| Term | Meaning |
|---|---|
| Container | A separately deployable/runnable unit (C4 terminology) |
| Component | A cohesive module within a container, with a defined responsibility |
| Effective data | The result of merging PlayerData with any PlayerOverride, override wins |
