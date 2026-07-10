# Infrastructure

Bicep templates for the Azure resources described in
`docs/decisions/0004-hosting-and-iac.md`. Composed as modules under
`bicep/modules/` rather than one flat file.

## What's provisioned

- Log Analytics workspace + Container Apps environment (`container-apps-environment.bicep`)
- Backend API as a Container App, Consumption plan, pulling from GHCR (`backend-container-app.bicep`)
- Frontend as a Static Web App, Free tier (`static-web-app.bicep`)

**Two regions, not one:** everything above lives in `swedencentral` (the
`location` parameter) except the Static Web App, which uses its own
`staticWebAppLocation` parameter (default `eastus2`) — `Microsoft.Web/staticSites`
only supports a small fixed region list (`centralus`/`eastus2`/`westus2`/`westeurope`/`eastasia`
as of writing) that `swedencentral` isn't on. Discovered by an actual failed
deployment (`LocationNotAvailableForResourceType`), not anticipated in
advance. `westeurope` (the only EU option on that list) was tried first as
the closest supported region to Sweden, but Azure is currently rejecting
new resources there entirely (a capacity restriction, not specific to this
subscription — see `aka.ms/locationineligible`) — `eastus2` is a deliberate
Tier 0 tradeoff to unblock deployment now, not a considered data-residency
decision. Only the *build/API service* location is affected; the frontend's
served static assets sit behind a global CDN regardless, and no personal
data is stored by this resource either way (that's Supabase, unaffected by
this choice). Revisit alongside `MVP-SCOPE.md`'s other pre-launch bright
lines (backups, legal docs) — retry `westeurope` once Azure's restriction
there lifts, if EU-only hosting matters by then.

## What's NOT provisioned here (deliberately)

- Supabase (Postgres + Auth) — provisioned via the Supabase dashboard/CLI,
  not Azure. This is a deliberate boundary from ADR-0004: the database/auth
  provider sits outside this IaC. Copy the connection string into GitHub
  Actions secrets (see below) — JWT validation needs no separate secret,
  it derives from the Supabase project URL alone via the JWKS endpoint
  (ADR-0017).
- The Static Web App's build/deploy pipeline itself — handled by the
  `Azure/static-web-apps-deploy` GitHub Action in `deploy.yml`, using a
  deployment token, not by this Bicep template.

## Environments (ADR-0006)

**Current reality (Tier 0, see `MVP-SCOPE.md`): only "dev" exists and is
deployed.** `deploy.yml` currently targets dev on every push to `main` —
there is no prod yet. The description below is the full Tier 1+ vision;
don't be misled by it into thinking prod already exists.

Two Supabase projects (the free plan's max), each with its own Container
App and Static Web App:

- **Dev**: `main.parameters.dev.json`. Tier 0's one and only environment
  right now — this is what you actually play and test on. Will also host
  the full test-data API (`/internal/test-data/*`) once that's built at
  Tier 1; Tier 0 only has REQ-806's much smaller local-test endpoint,
  gated the same way but scoped to the ephemeral CI stack, not this
  deployed environment. **Deploys automatically** via `deploy.yml` on
  every push to `main` (currently) — a separate `deploy-dev` job in
  `ci.yml`, keeping E2E always current against a deployed dev, is Tier 1
  (the committed `ci.yml` is Tier 0-shaped: local-stack E2E, no deployed
  environment involved at all).
- **Prod**: `main.parameters.json`. **Does not exist yet.** Created at
  Tier 1's bright line (a real user besides you) — at that point,
  `deploy.yml` gains a prod job/target, and this becomes the environment
  with backups, alerting, and no test-data API at all (excluded by an
  environment check in `Program.cs`, not just a permission check).

Resource naming follows `{appName}-{resource-type}-{environmentTag}`
throughout (e.g. `xg-arcade-api-dev` — exists now; `xg-arcade-api-prod` —
Tier 1), including resource groups (`xg-arcade-dev-rg` — exists now;
`xg-arcade-prod-rg` — Tier 1) — deliberately symmetric so "which
environment is this?" is never ambiguous from a resource name alone.

**Syncing game data between prod and dev (ADR-0009):** two directions,
sharing one allowlist (`infra/scripts/lib/game-data-tables.sh`) so they can
never drift apart on what's safe to move — only football/game reference
data (players, clubs, trophies, grid templates), never results (`Guess`,
`Round`, `GridInstance`/`GridCell`) or customer data (`User`,
`NotificationPreference`, `League`, `LeagueMembership`), regardless of direction.

- **Recommended workflow**: build/curate game data in dev, then run
  `infra/scripts/promote-dev-to-prod.sh` (or the `promote-dev-to-prod`
  GitHub Actions workflow) to ship it to prod.
- **Fallback workflow**: if prod's game data changed directly and dev
  needs to catch up, run `infra/scripts/sync-prod-to-dev.sh` (or the
  `sync-prod-to-dev` workflow) instead.

Both are manual-only by design and both support `--dry-run`. The
prod-writing direction (`promote-dev-to-prod.sh`) requires typing
`promote to prod` to confirm rather than just `sync`, as deliberate extra
friction given it writes to what real users are actively playing against.

## Backups (REQ-901 — Supabase free tier has none)

Confirmed directly against Supabase's docs (2026-07-05): free-tier projects
get zero automated backups, not limited ones. `backup-database.yml` runs
daily and uploads a `pg_dump` as a GitHub Actions artifact, retained 14 days.

**Restore procedure** (test this manually at least once before relying on it):

```bash
# Download the artifact from the GitHub Actions run, then:
pg_restore --clean --if-exists --dbname="$TARGET_DATABASE_URL" backup-TIMESTAMP.dump
```

Restore to a fresh Supabase project first if verifying a backup, rather
than restoring directly over a live database.

## Failure alerting (REQ-902)

GitHub's own failed-workflow email notifications cover this at zero
additional cost — but confirm they're actually enabled: Settings →
Notifications → Actions. Do a deliberate test (temporarily break a step in
any workflow) to confirm a notification actually arrives before relying on
this for round generation or backups.

## Data provider terms of service (ADR-0008)

Before public launch: email API-Football support to get written
confirmation that this project's use (a gameplay product, cached
permanently, not resold or redistributed as raw data) is acceptable under
their terms. Their own terms explicitly invite this ("if you have any
doubts... contact us directly by email") and separately list "fantasy
soccer games" as an intended use case — this is a confirmation step, not
an anticipated problem. Keep the response on file.

## Required secrets (set as GitHub Actions repository secrets)

Shared across both environments (one Azure OIDC identity, granted
Contributor on both resource groups):

| Secret | Used for |
|---|---|
| `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | OIDC federated login via `azure/login@v2` — no client secret needed |
| `RESEND_API_KEY` | Used by `XGArcade.Email` for direct API calls (product notifications). Also used as the SMTP password when configuring Supabase Auth's custom SMTP — not stored in this repo either way |
| `INTERNAL_JOB_TOKEN` | Shared bearer token authorizing calls to internal endpoints (`ci.yml`'s test-data reset, `sync-players.yml`, `generate-round.yml`) — generate any long random string and use the same value everywhere |
| `GHCR_TOKEN` | GitHub PAT (classic or fine-grained, `read:packages` scope) used as `deploy.yml`'s `registryPassword` for the Container App — **not** `GITHUB_TOKEN`. `GITHUB_TOKEN` expires shortly after the workflow run ends, but a scale-to-zero Container App (`minReplicas: 0`) needs to re-authenticate to GHCR on every cold start, which can happen long after that. Deploying with `GITHUB_TOKEN` succeeds but the app then fails with `ImagePullBackOff` on its first cold start after the token expires — found the hard way on S-002's first real deploy, see `NOTES.md`. Username stays `github.actor`, only the password needs to be this PAT |

Prod-specific:

| Secret | Used for |
|---|---|
| `PROD_AZURE_RESOURCE_GROUP` | Target resource group for `deploy.yml` |
| `PROD_DATABASE_CONNECTION_STRING` | Production Supabase Postgres connection string — also used by `backup-database.yml` and as the "prod" side of `sync-prod-to-dev.yml`/`promote-dev-to-prod.yml`. Must be the **.NET/ADO.NET keyword=value format** (`Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`), not the **URI** form (`postgresql://...`) Supabase's dashboard shows by default — Npgsql can't parse the URI form (see `NOTES.md`) |
| `PROD_SUPABASE_URL` | The production Supabase project's URL (Settings → API) — the backend calls its Auth REST API to mediate signup/login (ADR-0013), and validates incoming JWTs against this project's JWKS endpoint (ADR-0017); no separate secret needed for that |
| `PROD_SUPABASE_ANON_KEY` | The production Supabase project's anon/publishable key (Settings → API) — publishable by Supabase's own design, not a true secret, but still **required**: `Program.cs` throws at startup if `Supabase:AnonKey` is unconfigured, and an empty value also fails Azure's Container App secret validation at deploy time |
| `PROD_AZURE_STATIC_WEB_APPS_API_TOKEN` | From the prod Static Web App resource |
| `PROD_BACKEND_HOSTNAME` | Used by `sync-players.yml`/`generate-round.yml` to call scheduled internal endpoints on production |

Dev-specific:

| Secret | Used for |
|---|---|
| `DEV_AZURE_RESOURCE_GROUP` | Target resource group for the Tier 1 `deploy-dev` job |
| `DEV_DATABASE_CONNECTION_STRING` | Dev Supabase Postgres connection string — used by `deploy.yml`'s `migrate-and-seed-database` job (S-005) to apply migrations and seed Tier 0 reference data on every push to `main`; also the "dev" side of `sync-prod-to-dev.yml`/`promote-dev-to-prod.yml`. Must be the **.NET/ADO.NET keyword=value format** (`Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`), not the **URI** form (`postgresql://...`) Supabase's dashboard shows by default — Npgsql can't parse the URI form and fails with `ArgumentException: Format of the initialization string does not conform to specification` (see `NOTES.md`) |
| `DEV_SUPABASE_URL` | The dev Supabase project's URL (Settings → API) — the backend calls its Auth REST API to mediate signup/login (ADR-0013), and validates incoming JWTs against this project's JWKS endpoint (ADR-0017); no separate secret needed for that. `ci.yml`'s local E2E stack doesn't need this at all — it runs with `Auth__Mode=local-e2e`, which swaps in a fake auth client instead (see S-004, `docs/backlog.md`) |
| `DEV_SUPABASE_ANON_KEY` | The dev Supabase project's anon/publishable key (Settings → API) — publishable by Supabase's own design, not a true secret, but still **required**: `Program.cs` throws at startup if `Supabase:AnonKey` is unconfigured, and an empty value also fails Azure's Container App secret validation in `deploy.yml`'s `deploy-infra` job (`ContainerAppSecretInvalid`) before the app is ever deployed |
| `DEV_AZURE_STATIC_WEB_APPS_API_TOKEN` | From the dev Static Web App resource |
| `DEV_BACKEND_HOSTNAME` | Used by `generate-round.yml` to call scheduled internal endpoints; also by Tier 1's `ci.yml` for the test-data reset call and E2E test target |
| `DEV_FRONTEND_HOSTNAME` | Fed to `deploy.yml` as the backend's CORS-allowed origin (S-002) — the one real cross-environment coupling in an otherwise one-way deploy. Also used by Tier 1's `ci.yml` for the E2E test target |
| `DEV_ADMIN_USER_IDS` (S-012) | Comma-separated Supabase auth user ids (the JWT `sub` claim) authorized to call `/admin/*` endpoints — fed to `Admin__UserIds` (`AdminAuthorizationHandler`, `implementation-document.md` §4). Not set means no admin endpoint succeeds for anyone; look up your own id under the dev Supabase project's Authentication → Users once you've signed up, and set this secret to it |

Note the corrected `GHCR_TOKEN` row in the shared-secrets table above:
earlier revisions of this doc said the automated workflows didn't need a
separate GHCR secret at all (`github.actor`/`GITHUB_TOKEN` was assumed
sufficient) — that was wrong in a way that only surfaces after the app's
first cold start post-deploy, see the row's own explanation. No separate
`GHCR_USERNAME` secret is needed; the manual command below's
`<ghcr-username>` placeholder is just `github.actor`'s value (your GitHub
username), not a secret.

`backend-container-app.bicep` now sets `ASPNETCORE_ENVIRONMENT` on the
Container App itself (S-008) — `"Production"` when `environmentTag == 'prod'`,
`"Dev"` otherwise. Neither this module nor `deploy.yml` set this before,
so the deployed dev Container App was silently running as `Production`
(ASP.NET Core's default when the variable is absent) regardless of
`environmentTag`, meaning every non-Production-only endpoint (COMP-09's
`/internal/test-data/force-close-round`, S-007's `/internal/grid/generate`)
was unreachable there (see `NOTES.md`). `"Dev"`, not `"Development"`,
deliberately: it still makes `IsProduction()` false, but doesn't also flip
on `IsDevelopment()`-gated code (e.g. `Auth:Mode=local-e2e`'s fake auth
client) in a real deployed environment.

## Email setup (manual, one-time, not in Bicep)

Per ADR-0005, email has two independent sending paths on one Resend account:

1. **Auth emails** (signup confirmation, password reset): in the Supabase
   dashboard, go to Authentication → Emails → SMTP Settings, and enable
   custom SMTP using Resend's SMTP credentials. This raises Supabase's
   default 2-emails/hour cap (which also can't reach real users at all) to
   30/hour, adjustable in Authentication → Rate Limits. Also edit the
   "Confirm signup" email template to include both `{{ .ConfirmationURL }}`
   (button) and `{{ .Token }}` (numeric code) — this is what satisfies
   REQ-703's "code or button" requirement.
2. **Product notification emails** (round results, deferred to Phase 2):
   `XGArcade.Email` calls Resend's HTTP API directly using `RESEND_API_KEY`
   — no Supabase involvement.

Set up SPF/DKIM for the sending domain in Resend's dashboard for both paths
— deliverability degrades badly without this, and Supabase's own production
checklist calls it out explicitly.

## Manual first deploy (before CI/CD is wired up)

**Tier 0 needs only the dev block below** — this is the environment that
actually exists right now.

```bash
az login
az group create --name xg-arcade-dev-rg --location swedencentral

az deployment group create \
  --resource-group xg-arcade-dev-rg \
  --template-file infra/bicep/main.bicep \
  --parameters infra/bicep/main.parameters.dev.json \
  --parameters containerImage="ghcr.io/<org>/<repo>-api:latest" \
               registryUsername="<ghcr-username>" \
               registryPassword="<ghcr-token>" \
               databaseConnectionString="<dev-supabase-connection-string>" \
               supabaseUrl="<dev-supabase-url>" \
               supabaseAnonKey="<dev-supabase-anon-key>" \
               internalJobToken="<internal-job-token>"
```

**No `supabaseJwtSecret` parameter** — JWT validation fetches Supabase's
public signing keys from its JWKS endpoint automatically, derived from
`supabaseUrl` alone (ADR-0017). If a live deploy shows Supabase's actual
JWKS path differs from the built-in default
(`/auth/v1/.well-known/jwks.json`), add `supabaseJwksPath="<path>"` to this
command (or `az containerapp update --set-env-vars
Auth__SupabaseJwksPath=<path>` directly against the running Container App)
— it's a plain (non-secret) override, no rebuild needed.

**Quote every value**, not just the ones that look like they need it — a
`.NET`-format Postgres connection string always contains `;` and usually a
space (`SSL Mode=Require`), and unquoted `;` is a bash command separator.
`deploy.yml` learned this the hard way (see `NOTES.md`): an unquoted
`databaseConnectionString=${{ secrets.DEV_DATABASE_CONNECTION_STRING }}`
silently split one `az deployment group create` invocation into several
broken commands, truncating the connection string and losing every
`--parameters` entry after it.

Tier 1 — same for prod, once it's created (swap the resource group and
parameters file):

```bash
az group create --name xg-arcade-prod-rg --location swedencentral

az deployment group create \
  --resource-group xg-arcade-prod-rg \
  --template-file infra/bicep/main.bicep \
  --parameters infra/bicep/main.parameters.json \
  --parameters containerImage="ghcr.io/<org>/<repo>-api:latest" \
               registryUsername="<ghcr-username>" \
               registryPassword="<ghcr-token>" \
               databaseConnectionString="<prod-supabase-connection-string>" \
               supabaseUrl="<prod-supabase-url>" \
               supabaseAnonKey="<prod-supabase-anon-key>" \
               internalJobToken="<internal-job-token>"
```

After this first manual pass, `deploy.yml` takes over for dev (every push
to `main`) — this is Tier 0's actual current behavior. Prod deploys and a
`deploy-dev` job splitting the two are Tier 1, added when prod is created.

## Cost reality check (verified mid-2026)

This is genuinely free at current hobby scale, with one dependency worth
knowing about rather than discovering by accident.

| Service | Free tier | Risk if exceeded / relevant caveat |
|---|---|---|
| Azure Container Apps (Consumption) | 180,000 vCPU-seconds + 360,000 GiB-seconds + 2M requests/month, per subscription | Only free while `minReplicas: 0` (the default in `backend-container-app.bicep`). Setting `minReplicas: 1` to avoid cold starts moves idle time outside the free grant — roughly $10–12/month for one small always-on replica |
| Azure Static Web Apps | 100 GB bandwidth/month, Free tier | Free indefinitely at this scale, no time limit, up to 10 free-tier Static Web Apps per subscription |
| GHCR (container registry) | Free for public repos; free storage allowance on private repos too | Not a concern at this scale |
| GitHub Actions | Unlimited minutes on public repos; 2,000 min/month free on private repos | Only matters if the repo is private — `ci.yml` and `deploy.yml` run on every push, worth watching if minutes become tight |
| Supabase (Postgres + Auth) | 500 MB database, free indefinitely | **Free projects pause after 7 days with no database activity**, going offline until manually resumed from the dashboard |
| Resend (email) | 3,000 emails/month, 100/day | Covers both Supabase's custom SMTP (auth emails) and direct API calls (product notifications) at current scale — see ADR-0005. Watch the 100/day ceiling specifically if a round-close notification burst goes out to many users at once |
| Wikidata (primary live-lookup source) | No small fixed daily cap — throttled by query time (60s/minute per IP, bursts to 120s/min), not request count. Verified directly against Wikidata's own docs | The primary source for all live lookups (ADR-0011) — simple single-player queries are fast, so this is enormous headroom compared to API-Football. Response times can be variable/slow under current WDQS load; calls use a timeout and fall back to API-Football rather than blocking indefinitely |
| API-Football (fallback live-lookup source; club crests deferred to Phase 2) | 100 requests/day, 10/minute | Only called when Wikidata times out, errors, or has no matching data (ADR-0011) — expected to be a small fraction of total lookups, not a coequal source. Grid generation (REQ-103) and guess-time verification (REQ-211) share one tracked daily counter for this fallback path specifically; guess-time lookups stop falling back to it past 80/day, reserving 20 for scheduled grid generation. Crest fetching isn't part of v1 usage at all, and when it is built (Phase 2), it's genuinely low-risk: API-Football's own docs confirm logo/crest calls don't count against the 100/day quota at all, and the universe of distinct clubs ever needed is small and largely static — see `requirements-document.md` §6 |

**The Supabase pause is the one real trap.** It's an availability issue, not
a cost issue — a live app can silently go dark for any user hitting it
after 7 quiet days. The scheduled `sync-players.yml` job (runs daily) has
the side effect of keeping the project active, which currently prevents
this — but that's an accidental dependency, not a designed safeguard. If
`sync-players.yml` is ever disabled, paused, or its schedule widened beyond
7 days, add an explicit keep-alive ping (a trivial scheduled query) rather
than relying on the sync job to do it implicitly.

Numbers above will drift over time — re-verify against each provider's
pricing page before relying on them for a real cost decision.

## Swapping hosting later

Per ADR-0004, the backend has no Container-Apps-specific code — it's a
plain container. To move it elsewhere: write a new module (or use another
provider's CLI/Terraform) that runs the same image, point `deploy.yml` at
it, and delete the `backend-container-app.bicep` module. `main.bicep` and
the frontend/database modules are unaffected.
