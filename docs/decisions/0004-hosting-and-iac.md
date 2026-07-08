# ADR-0004: Hosting on Azure Container Apps + Static Web Apps, Bicep for IaC, Supabase for data/auth

- **Status:** Accepted
- **Date:** 2026-07-04
- **Related requirements:** REQ-602 (cost envelope)
- **Related components:** CONT-01 (Web Frontend), CONT-02 (Backend API), CONT-03 (Database)

## Context

Earlier drafts of the implementation document proposed Vercel (frontend) and
an unresolved choice between Fly.io/Azure (backend), left as an open
question. The developer has significant existing Azure and Bicep experience
(see prior work: GitLab CI/CD deploying Bicep templates to Azure). Given
that experience, defaulting to unfamiliar platforms (Fly.io) has no real
upside, and using Azure lets existing skills and patterns transfer directly.
The hard constraint remains: must run at effectively zero cost at MVP scale,
and any single vendor choice must be easy to replace later without a
rewrite.

## Decision

- **Backend**: containerized ASP.NET Core app deployed to **Azure Container
  Apps** (Consumption plan). The app is a standard Docker container with no
  Container-Apps-specific code dependencies.
- **Container registry**: **GitHub Container Registry (GHCR)**, not Azure
  Container Registry — ACR has no free tier beyond a short trial; GHCR is
  free and Container Apps can pull from it with a registry credential.
- **Frontend**: **Azure Static Web Apps** (Free tier).
- **Database + Auth**: **Supabase** (Postgres + built-in Auth). Not an Azure
  service, but there is no equivalent free-forever managed Postgres on
  Azure. Bundling auth with the database removes a separate auth-provider
  decision (previously an open question) with negligible added lock-in,
  since the underlying data is standard Postgres.
- **IaC**: **Bicep**, composed as modules (Container Apps environment,
  Container App, Static Web App) rather than one flat template, consistent
  with the developer's established preference for composition over
  conditional branching.
- **CI/CD**: **GitHub Actions** (the repo is on GitHub, not GitLab).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Vercel + Fly.io (original draft) | Both have generous free tiers, minimal ops | Neither leverages existing Azure/Bicep expertise; two unfamiliar platforms to learn | No advantage over Azure given the developer's background |
| Azure App Service (Free F1 tier) for backend | Simple, classic Azure PaaS | F1 tier has ~60 CPU-minutes/day, no custom domains/SSL practically usable, sleeps aggressively — too limited for a scheduled-job-driven app | Consumption-plan Container Apps gives a much more usable free allowance |
| Azure Container Registry for images | Native Azure integration | No meaningful free tier (Basic SKU is a paid, low-cost tier, not free) | GHCR achieves the same result at zero cost |
| Azure Database for PostgreSQL | Fully Azure-native, one youtube support surface | No indefinite free tier (only a time-limited trial credit) | Fails the free-now constraint; Supabase is functionally equivalent Postgres |

## Consequences

- Positive: infrastructure work reuses existing Azure/Bicep skills directly;
  everything is containerized so the backend can move to any container host
  later by changing only the IaC module, not application code; free at
  current scale
- Negative / trade-offs accepted: the database/auth provider (Supabase) sits
  outside Azure, so there are two infrastructure surfaces (Azure via Bicep,
  Supabase via its own dashboard/CLI) instead of one; Container Apps
  Consumption plan has cold-start latency after scale-to-zero, which is
  acceptable for a low-traffic MVP but worth revisiting if usage grows
- Follow-up: if traffic or cost ever justifies it, evaluate migrating
  Supabase's Postgres into an Azure-hosted Postgres instance — since it's
  standard Postgres, this is a data migration, not an application rewrite.
  **Update, 2026-07-07:** a documentation review found the Bicep templates
  had drifted to stale API versions (dated 2023-2024) despite never having
  been deployed — fixed to current stable versions as of that review. Since
  these templates won't be exercised until first deploy, re-verify API
  versions again at that point rather than trusting this fix indefinitely.

## For AI agents

Do not introduce Container-Apps-specific or Azure-specific code into
`XGArcade.Api` or `XGArcade.Core` (e.g. Azure SDK calls for business logic).
The application must remain a plain container that happens to be deployed
to Azure; hosting-specific configuration belongs only in `/infra` and
environment variables. If a task seems to require Azure-specific application
code, stop and flag it — that would violate the "easy to swap" goal this
ADR exists to protect.
