# Setup Guide — External Accounts & Services

> **MVP note:** per `MVP-SCOPE.md`, the MVP needs only **one** Supabase
> project (this is your "dev" project — skip the second/prod one), and
> does **not** need Resend at all (turn off Supabase Auth's email
> confirmation requirement instead of setting up email sending). Steps
> below are marked accordingly — skip the marked ones until you're
> actually past MVP and adding Tier 1 work.

A step-by-step walkthrough for everything in `TODO.md`'s "Before writing
any code" section. Ordered so each step's outputs feed the next one —
follow it top to bottom rather than jumping around.

## 1. GitHub (repo)

You already have an account.

1. Create a new repository (public or private — public gets unlimited
   free GitHub Actions minutes and free GHCR, see `infra/README.md`'s cost table)
2. Push everything from `xg-arcade-docs.zip` to it, preserving the folder
   structure exactly (`.claude/`, `docs/`, `infra/`, `.github/`, root files)
3. Nothing else needed here yet — come back after step 5 to add secrets

## 2. Supabase (database + auth)

**MVP: create only project #1 — this is your "dev" project (Tier 0's one
environment).** Skip project #2 (prod) until Tier 1's bright line (a real
user besides you).

Create **two** projects eventually — the free plan allows exactly two,
which is exactly what ADR-0006 needs (dev + prod).

1. Sign up at supabase.com, create project **#1** — this is **dev**,
   Tier 0's one and only environment
2. (Tier 1) create project **#2** — this becomes **prod**, once you need it
3. From Project Settings on whichever project(s) you've created:
   - **Connection string** (Settings → Database) — save it
   - **JWT secret** (Settings → API) — save it
   - **Project URL** and **anon/public key** (Settings → API) — save both;
     the backend calls Supabase Auth's REST API directly to mediate
     signup/login (ADR-0013), rather than the frontend calling Supabase
     itself. The anon key is publishable by Supabase's own design (safe in
     a frontend bundle too), not a true secret
4. Don't touch Auth/SMTP settings yet — that needs Resend first (step 3)

## 3. Resend (email)

**MVP: skip this whole section.** Instead, turn off Supabase Auth's
"confirm email" requirement in project settings (Authentication → Providers
→ Email → uncheck "Confirm email"). MVP accounts work immediately without
any email flow at all. Come back to this section only when adding Tier 1's
email confirmation (REQ-701-705, ADR-0005).

**No domain needed yet.** Unlike most email providers, Resend has no
sandbox/approval restriction on recipients — you can send real emails to
real addresses immediately using their shared `onboarding@resend.dev`
sender, before verifying any domain of your own. The only thing a domain
unlocks is a branded sender address and better deliverability. Buy a
domain when you want to look professional to real outside users, not
before — nothing in this guide blocks on it. (Azure Static Web Apps and
Container Apps also give you free default subdomains, so the same applies
to hosting — no domain needed to deploy or test "prod.")

1. Sign up at resend.com, verify a sending domain (or use their test/sandbox
   sending option to start — fine for early development, not for real users)
2. Grab the API key
3. In **both** Supabase projects: Authentication → Emails → SMTP Settings →
   enable custom SMTP using Resend's SMTP credentials
4. In **both** Supabase projects: edit the "Confirm signup" email template
   to include both `{{ .ConfirmationURL }}` and `{{ .Token }}` (satisfies
   REQ-703 — code or button)
5. Set up SPF/DKIM for your sending domain in Resend's dashboard (skip if
   using their sandbox domain for now)

## 4. API-Football (player data)

**Tier 1 — skip this whole section for MVP.** Per the corrected Tier 0
design in `MVP-SCOPE.md`, Tier 0 uses Wikidata only (no account needed,
public endpoint) for full historical accuracy on a small hand-curated club
list. Come back here when adding API-Football as a Tier 1 fallback source.

1. Sign up for the free tier at api-football.com
2. Grab the API key
3. **Do this before relying on it, not after:** email their support asking
   for written confirmation that this project's use case (a gameplay
   product, data cached permanently, not resold) is fine under their
   terms — see ADR-0008. A draft is ready at
   `docs/decisions/correspondence/api-football-confirmation-email.md` —
   review it, send it, and save their reply alongside it in the same folder.

## 5. Azure (hosting)

**MVP: create only the dev resource group** — this is Tier 0's one and
only environment (see `MVP-SCOPE.md` for why it's named "dev," not
"prod"). Skip prod entirely until Tier 1 creates it for real.

1. Create or use an existing Azure subscription
2. Create resource group `xg-arcade-dev-rg` (and, later for Tier 1,
   `xg-arcade-prod-rg`)
3. Set up OIDC federated login for GitHub Actions (no long-lived secret needed):
   - Create an App Registration in Azure AD
   - Add a **federated credential** on it scoped to your GitHub repo
     (Azure Portal → App registration → Certificates & secrets → Federated
     credentials → GitHub Actions), entity type "branch", value `main`
     — this one identity covers both environments once both exist
   - Assign the app **Contributor** role on the resource group(s) you
     created (add prod's when Tier 1 creates it)
   - Note the **Application (client) ID**, **Directory (tenant) ID**, and
     your **Subscription ID** — you'll need all three next

## 6. Wire it together — GitHub repo secrets

**MVP: skip `RESEND_API_KEY` and every `PROD_*` secret** until Tier 1.

Repo → Settings → Secrets and variables → Actions. Add each of these
(exact names, matching `infra/README.md`'s table — everything environment-
specific is prefixed `PROD_` or `DEV_`, nothing else):

**Shared (MVP needs these):**

| Secret | Value comes from |
|---|---|
| `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | Step 5 |
| `INTERNAL_JOB_TOKEN` | Make one up — any long random string |

**Shared (Tier 1 — skip for MVP):**

| Secret | Value comes from |
|---|---|
| `RESEND_API_KEY` | Step 3, once you're doing email confirmation |

**Dev (MVP needs these — this is Tier 0's one environment):**

| Secret | Value comes from |
|---|---|
| `DEV_AZURE_RESOURCE_GROUP` | Step 5 (`xg-arcade-dev-rg`) |
| `DEV_DATABASE_CONNECTION_STRING` | Step 2, your one Supabase project |
| `DEV_SUPABASE_JWT_SECRET` | Step 2, your one Supabase project |
| `DEV_SUPABASE_URL` / `DEV_SUPABASE_ANON_KEY` | Step 2, your one Supabase project (Settings → API) |
| `DEV_AZURE_STATIC_WEB_APPS_API_TOKEN` | Comes from step 7, add it after |
| `DEV_BACKEND_HOSTNAME` / `DEV_FRONTEND_HOSTNAME` | Comes from step 7, add it after |

**Prod (Tier 1 — skip for MVP; add when creating the real prod environment):**

| Secret | Value comes from |
|---|---|
| `PROD_AZURE_RESOURCE_GROUP` | `xg-arcade-prod-rg`, created at Tier 1 |
| `PROD_DATABASE_CONNECTION_STRING` | A second Supabase project, created at Tier 1 |
| `PROD_SUPABASE_JWT_SECRET` | Same second Supabase project |
| `PROD_SUPABASE_URL` / `PROD_SUPABASE_ANON_KEY` | Same second Supabase project (Settings → API) |
| `PROD_AZURE_STATIC_WEB_APPS_API_TOKEN` | From the prod deploy, once it exists |
| `PROD_BACKEND_HOSTNAME` | From the prod deploy, once it exists |

## 7. First deploy (manual, before CI/CD takes over)

Run this once by hand for **dev — Tier 0's one environment**:

```bash
az login
az deployment group create \
  --resource-group xg-arcade-dev-rg \
  --template-file infra/bicep/main.bicep \
  --parameters infra/bicep/main.parameters.dev.json \
  --parameters containerImage=ghcr.io/<org>/<repo>-api:latest \
               registryUsername=<your-github-username> \
               registryPassword=<a-github-PAT-with-read:packages> \
               databaseConnectionString=<dev-supabase-connection-string> \
               supabaseJwtSecret=<dev-supabase-jwt-secret> \
               supabaseUrl=<dev-supabase-url> \
               supabaseAnonKey=<dev-supabase-anon-key>
```

And once for **prod** (Tier 1 — skip for MVP; same command, swap resource
group + parameters file + values):

```bash
az deployment group create \
  --resource-group xg-arcade-prod-rg \
  --template-file infra/bicep/main.bicep \
  --parameters infra/bicep/main.parameters.json \
  --parameters containerImage=ghcr.io/<org>/<repo>-api:latest \
               registryUsername=<your-github-username> \
               registryPassword=<a-github-PAT-with-read:packages> \
               databaseConnectionString=<prod-supabase-connection-string> \
               supabaseJwtSecret=<prod-supabase-jwt-secret> \
               supabaseUrl=<prod-supabase-url> \
               supabaseAnonKey=<prod-supabase-anon-key>
```

This won't fully succeed until `backend/Dockerfile` actually exists and an
image has been pushed to GHCR — that's Claude Code's first job (see
`CLAUDE.md`'s "Getting started" section), not something to do by hand.
Realistic order: get the trivial first slice built and pushed via `ci.yml`,
*then* come back and run this.

For MVP, only the dev deploy matters (it's Tier 0's one environment) —
once it succeeds, grab the outputs (backend hostname, Static Web App
token/hostname) and fill in the dev secrets left pending in step 6.
`deploy.yml` then redeploys dev automatically on every push to `main`.
The prod deploy above is Tier 1 — it doesn't apply until you've created a
second Supabase project and prod resource group, at the bright line
described in `MVP-SCOPE.md`.

## 8. Verify before building for real

**MVP checklist:**

- [ ] `ci.yml` passes on a trivial commit (it's already Tier 0-shaped:
  unit tests + local-stack E2E, no dev deploy job needed — see its header comment)
- [ ] `deploy.yml` successfully deploys to dev (Tier 0's one environment) on push to `main`
- [ ] GitHub Actions failure-notification emails are enabled (Settings →
  Notifications → Actions) — cheap to confirm now even though formal
  alerting (REQ-902) is Tier 1

**Tier 1 checklist (once you're past MVP):**

- [ ] `infra/scripts/sync-prod-to-dev.sh --dry-run` runs without error
- [ ] `infra/scripts/promote-dev-to-prod.sh --dry-run` runs without error
  (this is the recommended day-to-day direction — see ADR-0009)

## 9. Claude Code — VS Code and GitHub setup

This is the local/computer path. (If you're phone-only, use Claude Code on
the web instead — claude.ai/code or the Code tab in the mobile app — no
install needed; see the earlier conversation on this.)

**VS Code extension** (the GUI panel):
1. Extensions view (`Cmd/Ctrl+Shift+X`) → search "Claude Code" → Install
   (published by Anthropic)
2. Click the Spark icon (editor toolbar or Activity Bar) → sign in with
   your Claude.ai account — Pro covers this, no separate API key needed

**CLI** (needed to actually run `claude` in the terminal — the extension
bundles its own copy for the chat panel only):
1. Install Node.js if you don't have it
2. `npm install -g @anthropic-ai/claude-code`
3. Open the integrated terminal in your project folder, run `claude`,
   sign in via the browser prompt (same Claude.ai account)

**GitHub** (so Claude Code can create branches, commits, and PRs on its own):
1. Install GitHub's CLI: `brew install gh` (Mac) or see cli.github.com for
   other OS
2. `gh auth login` — one-time browser-based login
3. That's it — Claude Code detects an authenticated `gh` automatically and
   uses it via natural language ("open a PR for this")

**Point it at this project:**
1. `git clone` (or `gh repo clone`) the repo you pushed the docs to
2. `File → Open Folder` in VS Code, select that cloned folder
3. Claude Code auto-reads `CLAUDE.md` and everything in `.claude/` from
   the repo root — no extra configuration needed, that's exactly what
   this whole doc set was built for

Optional, later: Anthropic also offers a GitHub Action integration
(`@claude` mentions directly in issues/PR comments trigger a session) —
not needed to start, worth adding once the core dev loop feels good.

