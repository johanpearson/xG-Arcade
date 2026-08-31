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
   - **Connection string** (Settings → Database) — save it. Supabase's
     dashboard defaults to showing this in **URI** form
     (`postgresql://user:pass@host:port/db`) — that format is **not**
     valid for Npgsql/EF Core, which needs ADO.NET keyword=value pairs
     instead. Switch the dashboard's connection-string tab to **.NET**
     (or build it by hand: `Host=<host>;Port=5432;Database=postgres;
     Username=postgres;Password=<password>;SSL Mode=Require;Trust Server
     Certificate=true`) before saving it as `DEV_DATABASE_CONNECTION_STRING`
     — pasting the URI form fails with an Npgsql
     `ArgumentException: Format of the initialization string does not
     conform to specification` as soon as anything actually opens a
     connection with it (see `NOTES.md`)
   - **Project URL** and **anon/public key** (Settings → API) — save both;
     the backend calls Supabase Auth's REST API directly to mediate
     signup/login (ADR-0013), rather than the frontend calling Supabase
     itself. The anon key is publishable by Supabase's own design (safe in
     a frontend bundle too), not a true secret — but it's still a required
     value: the backend throws at startup if `Supabase:AnonKey` isn't
     configured (`Program.cs`), so an empty `DEV_SUPABASE_ANON_KEY` secret
     also fails `deploy.yml`'s `deploy-infra` job (Azure rejects an empty
     Container App secret value outright, before the app even starts).
     There's no separate "JWT secret" to copy — JWT validation fetches
     Supabase's public signing keys from its JWKS endpoint automatically
     from the Project URL alone (ADR-0017)
   - **`service_role` key** (Settings → API, same page as the anon key —
     labeled `service_role`/`secret`) — save it too, as
     `DEV_SUPABASE_SERVICE_ROLE_KEY`. Unlike the anon key above, **this one
     is a true secret** (bypasses Row Level Security entirely) — REQ-710's
     self-service account deletion (ADR-0026) uses it to call Supabase's
     Admin API and delete the underlying identity; never put it anywhere a
     frontend bundle could read it. Also required at startup
     (`Supabase:ServiceRoleKey`, `Program.cs`)
4. Don't touch Auth/SMTP settings yet — that needs Resend first (step 3)
5. **Enable Anonymous Sign-ins (required for guest play, REQ-717/ADR-0036):**
   Authentication → Sign In / Providers → **Anonymous Sign-ins**, turned
   **on** — off by default on a new Supabase project, and easy to miss
   since nothing else in this setup flow touches it. Guest play
   (`POST /auth/guest`) fails outright with "Could not start a guest
   session" until this is on — a real gap this doc never called out before
   a live deployment hit it. Turning it on surfaces Supabase's own warning
   recommending a captcha be added — see step 6 below for that.
6. **Captcha hardening — project-wide, not guest-only (Tier 1/2, pulled
   forward alongside guest play — REQ-717/REQ-701/REQ-710,
   ADR-0036/ADR-0037): the backend pass-through on all four call sites
   (`AuthController.Guest`/`Signup`/`Login`/`DeleteAccount`, calling
   `SignInAnonymouslyAsync`/`SignUpAsync`/`SignInWithPasswordAsync`) and
   the frontend's Turnstile widget/token acquisition
   (`frontend/src/lib/turnstile.ts`, `AuthScreen.tsx`,
   `DeleteAccountScreen.tsx`) are fully implemented as of 2026-07-25 — this
   step's manual Cloudflare/Supabase dashboard configuration is the only
   remaining piece, not a precondition to complete before the code lands.**
   **Correction (2026-07-25):** this step was originally written as if
   Supabase's "Enable Captcha Protection" toggle only affected guest
   account creation. It does not — per a live incident written up in
   `NOTES.md`'s 2026-07-25 entry, this is a single **project-wide**
   dashboard toggle covering every Supabase Auth (`gotrue`) endpoint that
   can create or authenticate an identity: anonymous sign-in (guest),
   `signup`, and `token?grant_type=password` (used by both `Login` and
   `DeleteAccount`'s password re-confirmation). Turning it on for guest
   creation alone — the original wording of this step — silently broke
   real password-based login and signup the first time it was enabled
   against a live project, because those code paths didn't send a captcha
   token at all. That gap is now closed: all four call sites send a
   Turnstile token, so enabling this toggle is safe to do once, below,
   without breaking any of them. Guest account creation (`POST
   /auth/guest`) uses Supabase's own Anonymous Sign-ins feature; enabling
   that in Supabase's dashboard (Authentication → Providers → Anonymous)
   surfaces its own warning recommending a captcha be enabled to prevent
   abuse — but the toggle you enable in response to that warning applies to
   signup and login too, not just to anonymous sign-ins. ADR-0037 answers
   that with Cloudflare Turnstile:
   - Sign up at cloudflare.com (free), add a Turnstile **site**
     (dash.cloudflare.com → Turnstile → Add site) — no domain ownership
     verification needed to get keys for local/dev use
   - Choose **Managed mode** when configuring the widget (Cloudflare's
     dashboard widget-mode options are Managed/Non-Interactive/Invisible —
     this is a property of the Turnstile *site* itself, set here, not
     something the frontend code can override afterward). **Corrected
     2026-07-25 (ADR-0037's third amendment, sign-in latency
     investigation):** this step originally said "invisible/managed mode,"
     matching REQ-717's original widget UX recommendation — that
     recommendation is now reversed to an always-visible checkbox
     (`size: 'normal'` in `frontend/src/lib/turnstile.ts`), so the site
     itself must be Managed (or Non-Interactive), never **Invisible** — a
     site configured as Invisible cannot show a widget at all regardless
     of what the frontend code requests, since that's enforced
     server-side by Cloudflare, not by the client's `size` parameter. **If
     an existing dev/prod Turnstile site was created under the original
     instruction and picked Invisible specifically**, go back into
     dash.cloudflare.com → Turnstile → that site's settings and switch its
     widget mode to Managed — the code change alone will not make a
     checkbox appear on an Invisible-type site.
   - Save the **site key** — this is public, safe in frontend code, and
     becomes the frontend's `VITE_TURNSTILE_SITE_KEY` build-time
     environment variable (same pattern as `VITE_API_BASE_URL` — see
     `frontend/src/lib/api.ts`). For the deployed dev environment, also
     save it as the `DEV_TURNSTILE_SITE_KEY` GitHub Actions secret —
     `deploy.yml`'s `deploy-frontend` job feeds it through to the same
     Vite build the same way `VITE_API_BASE_URL` already is (see
     `infra/README.md`'s secrets table)
   - Save the **secret key** — this is a true secret, but it is **never**
     configured in this application's backend or its secrets. Paste it
     directly into Supabase's own Auth dashboard settings (Authentication
     → Attack Protection / Bot and Abuse Protection, wherever the current
     Supabase dashboard exposes captcha provider configuration) —
     Supabase verifies the token against Cloudflare directly, this
     backend never calls Cloudflare itself (see ADR-0037)
7. **Create the avatar upload bucket (REQ-722/ADR-0087, S-180):** Storage →
   New bucket → name it **`avatars`** (matches the code default,
   `Supabase:AvatarBucketName` — override that config value instead if a
   different bucket name is ever needed). The backend writes to/deletes
   from this bucket using the `service_role` key saved above (never the
   anon key), so it does not need any public bucket/RLS policy for
   `POST /users/me/avatar` (S-180) itself to work — a public read policy is
   only needed once REQ-517/S-181's admin approval flow needs to serve an
   *approved* image back out, not before. Skipped entirely in `ci.yml`'s
   local E2E stack (`Auth:Mode=local-e2e` swaps in a stub, same as Supabase
   Auth — see `ServiceRegistration.AddAvatarStorageServices`), so this step
   only matters for a real dev/prod deployment.

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
Note this is unrelated to xG Predict, which uses football-data.org instead
(§4a below) — API-Football was tried for xG Predict first (ADR-0094) but
its free tier turned out to exclude the current season entirely, so it was
swapped out (ADR-0099) before this section's own Tier 1 fallback trigger
ever fired.

1. Sign up for the free tier at api-football.com
2. Grab the API key
3. **Do this before relying on it, not after:** email their support asking
   for written confirmation that this project's use case (a gameplay
   product, data cached permanently, not resold) is fine under their
   terms — see ADR-0008. A draft is ready at
   `docs/decisions/correspondence/api-football-confirmation-email.md` —
   review it, send it, and save their reply alongside it in the same folder.

## 4a. football-data.org (xG Predict fixtures)

**Not Tier 1 — xG Predict (REQ-1301-1305, ADR-0099) needs this now.** It is
a precondition scoped only to that game (see `MVP-SCOPE.md`'s xG Predict
note): fixtures/live-score data that Wikidata cannot provide, and that
API-Football's free tier turned out not to provide either (ADR-0099).
Without a token configured, every `/internal/generate-round` call for
`xg-predict` fails closed
(`FootballDataClientException: "football-data.org is not configured on
this environment yet."`).

1. Sign up for the free tier at football-data.org
2. Grab the API token
3. **Done (2026-08-31):** the real terms of service have been read —
   no commercial-use restriction found, tied to a specific tier or
   otherwise (ADR-0099's Decision item 4 status update has the full
   analysis; the terms themselves are saved at
   `docs/decisions/correspondence/football-data-org-terms.md`). One real
   caveat worth knowing: §9.1 says historical data can't keep being
   referenced once the subscription is cancelled — not a blocker while
   the free tier stays active, but re-check before ever letting the
   account lapse.
4. Set the token as the `FOOTBALL_DATA_API_KEY` GitHub Actions repository
   secret (shared across environments, not `DEV_`/`PROD_`-prefixed — see
   `infra/README.md`). The next push to `main` (or a manual `deploy.yml`
   run) picks it up automatically; no code change needed.
5. Add the required "Football data provided by the Football-Data.org API"
   attribution somewhere in the frontend before public launch (a real ToS
   requirement, not optional — see `TODO.md`).

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
| `INCIDENT_REPORT_PAT` (REQ-903, ADR-0064) | github.com → Settings → Developer settings → Fine-grained tokens → Generate new token. Repository access: "Only select repositories" → this repo only. Repository permissions: `Issues` → **Read and write** (this also auto-selects `Metadata` → **Read**, required, leave it) — no other permission. Recommended expiration: 90 days. This powers the in-app "Report a problem" feature (`Core.IncidentReporting`) turning a player's bug report into a real GitHub issue here — optional until you're ready to test that feature; `POST /incidents` just fails closed (a clear error to the player) until this secret exists. Test against a throwaway/test repo first, not this one, before trusting it in front of real players (see `docs/decisions/0064-backend-mediated-github-incident-reporting.md`) |

**Dev (MVP needs these — this is Tier 0's one environment):**

| Secret | Value comes from |
|---|---|
| `DEV_AZURE_RESOURCE_GROUP` | Step 5 (`xg-arcade-dev-rg`) |
| `DEV_DATABASE_CONNECTION_STRING` | Step 2, your one Supabase project |
| `DEV_SUPABASE_URL` / `DEV_SUPABASE_ANON_KEY` / `DEV_SUPABASE_SERVICE_ROLE_KEY` | Step 2, your one Supabase project (Settings → API) |
| `DEV_AZURE_STATIC_WEB_APPS_API_TOKEN` | Comes from step 7, add it after |
| `DEV_BACKEND_HOSTNAME` / `DEV_FRONTEND_HOSTNAME` | Comes from step 7, add it after |

**Prod (Tier 1 — skip for MVP; add when creating the real prod environment):**

| Secret | Value comes from |
|---|---|
| `PROD_AZURE_RESOURCE_GROUP` | `xg-arcade-prod-rg`, created at Tier 1 |
| `PROD_DATABASE_CONNECTION_STRING` | A second Supabase project, created at Tier 1 |
| `PROD_SUPABASE_URL` / `PROD_SUPABASE_ANON_KEY` / `PROD_SUPABASE_SERVICE_ROLE_KEY` | Same second Supabase project (Settings → API) |
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
  --parameters containerImage="ghcr.io/<org>/<repo>-api:latest" \
               registryUsername="<your-github-username>" \
               registryPassword="<a-github-PAT-with-read:packages>" \
               databaseConnectionString="<dev-supabase-connection-string>" \
               supabaseUrl="<dev-supabase-url>" \
               supabaseAnonKey="<dev-supabase-anon-key>" \
               supabaseServiceRoleKey="<dev-supabase-service-role-key>"
```

(JWT validation needs no separate secret parameter — it derives from
`supabaseUrl` alone, ADR-0017. If Supabase's JWKS endpoint path ever needs
overriding, add `supabaseJwksPath="<path>"` to this command — see
`infra/README.md`.)

**Quote every value** — a `.NET`-format Postgres connection string contains
`;` and usually a space, and unquoted `;` is a bash command separator that
will silently truncate the command and drop every parameter after it (see
`NOTES.md`).

And once for **prod** (Tier 1 — skip for MVP; same command, swap resource
group + parameters file + values):

```bash
az deployment group create \
  --resource-group xg-arcade-prod-rg \
  --template-file infra/bicep/main.bicep \
  --parameters infra/bicep/main.parameters.json \
  --parameters containerImage="ghcr.io/<org>/<repo>-api:latest" \
               registryUsername="<your-github-username>" \
               registryPassword="<a-github-PAT-with-read:packages>" \
               databaseConnectionString="<prod-supabase-connection-string>" \
               supabaseUrl="<prod-supabase-url>" \
               supabaseAnonKey="<prod-supabase-anon-key>" \
               supabaseServiceRoleKey="<prod-supabase-service-role-key>"
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

