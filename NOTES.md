# Notes

A running log of small, practical context that isn't worth a full ADR but
is worth remembering — gotchas discovered while building, quirks of a
third-party service, things that took longer than expected, "don't do X,
here's why" reminders. Think of this as the difference between:

- **`docs/decisions/*.md` (ADRs)**: formal, durable decisions with
  alternatives considered — "we chose X over Y because Z"
- **`docs/CHANGELOG.md`**: what changed in the *documentation*, dated
- **`NOTES.md` (this file)**: informal, accumulated context that doesn't
  fit either — the stuff you'd otherwise only remember by re-reading old
  chat history

Add an entry whenever something surprises you enough that you'd want to
have known it going in. Prune entries that stop being relevant (e.g. a
workaround for a bug that got fixed upstream) rather than letting this
grow forever — unlike the CHANGELOG, this file doesn't need to preserve
history, just current usefulness.

## Format

```
### YYYY-MM-DD — short title
What happened / what to know. Keep it to a few sentences.
```

## Entries

### 2026-07-09 — Microsoft.AspNetCore.OpenApi dropped from XGArcade.Api (S-001)
The default `dotnet new webapi` template pulls in `Microsoft.AspNetCore.OpenApi`
10.0.9, which transitively depends on `Microsoft.OpenApi` 2.0.0 — flagged by
NuGet (NU1903) as a known high-severity vulnerability
(GHSA-v5pm-xwqc-g5wc) across the *entire* 2.x line (checked every 2.x patch
release up to 2.6.1, all vulnerable). The 3.x line fixes it, but breaks the
AspNetCore.OpenApi 10.0.9 source generator (`OpenApiXmlCommentSupport`),
which was compiled against 2.x's API shape (`CS0200: 'IOpenApiMediaType.Example'
cannot be assigned to`). Since nothing in Tier 0 needs OpenAPI generation yet,
removed the package entirely rather than pinning around it — no
`AddOpenApi()`/`MapOpenApi()` calls, no Swagger UI. **If a future story needs
this** (e.g. `implementation-document.md` §4's planned typed API client,
"possibly generated via OpenAPI"), check whether a compatible
`Microsoft.AspNetCore.OpenApi` version exists yet before re-adding it — don't
just re-run `dotnet add package Microsoft.AspNetCore.OpenApi` and assume it
still works.

### 2026-07-09 — GITHUB_TOKEN as the Container App's GHCR registry password breaks on cold start
After every other deploy.yml blocker was cleared (lowercase tag, Bicep
decorators, region, provider registration) and `deploy-infra` finally
succeeded, the deployed app itself still didn't work: the backend
Container App got stuck `ImagePullBackOff` / "Persistent Image Pull
Errors" trying to pull its own just-pushed image, confirmed via Azure
Portal's Container App system event log (Container Apps → app → Log
stream → System logs — the *console* log stream is useless here since it
can't attach to a container that never starts; the system log stream
shows platform-level events like image pulls instead).

Root cause: `deploy.yml` passed `secrets.GITHUB_TOKEN` as `registryPassword`
for the Container App's GHCR credential. `GITHUB_TOKEN` is scoped to the
workflow run and expires shortly after it finishes — fine for the
`docker/login-action` push step earlier in the same run, but wrong for a
credential the platform needs to keep re-using. The Container App has
`minReplicas: 0` (scale-to-zero, keeps Tier 0 free), so it re-pulls and
re-authenticates to GHCR on *every* cold start, which can happen minutes,
hours, or days after the deploy workflow that set the credential already
finished — by which point the token is dead and every cold start fails
forever (`ContainerBackOff`, retry count climbing, no recovery without a
new deploy).

Fixed by switching `registryPassword` to a new secret, `GHCR_TOKEN` — a
long-lived GitHub PAT (classic or fine-grained, `read:packages` scope),
not tied to a workflow run. `infra/README.md` had actually already named
`GHCR_TOKEN` as a secret, but wrongly scoped it to "manual first deploy
only," on the same wrong assumption that `GITHUB_TOKEN` was fine for the
automated path — corrected there too. **This class of bug (ephemeral
workflow token used as a credential a scale-to-zero/serverless resource
needs to keep re-authenticating with) is worth remembering generally**,
not just for this one secret — any future `minReplicas: 0` resource
pulling from a private registry needs a long-lived credential, not
`GITHUB_TOKEN`, even if `GITHUB_TOKEN` looks like it works at deploy time.

### 2026-07-09 — Static Web Apps doesn't support swedencentral
Once the Bicep decorator syntax errors above were fixed, `deploy-infra`
compiled cleanly and actually called `az deployment group create` for the
first time — which failed with `LocationNotAvailableForResourceType`:
`Microsoft.Web/staticSites` doesn't support `swedencentral` at all (its
supported list is `centralus`/`eastus2`/`westus2`/`westeurope`/`eastasia`).
Everything else in the template (Container Apps environment, the backend
Container App) does support `swedencentral` — only the Static Web App
resource type has this restriction. The module's own doc comment
(`static-web-app.bicep`) had already flagged "Static Web Apps only supports
a subset of regions" as a known caveat, but `main.bicep` still passed it
the same shared `location` as everything else — the caveat was documented
but never actually acted on. Fixed by giving `main.bicep` a second
parameter, `staticWebAppLocation` (default `westeurope`, the closest
supported region to Sweden), used only for that one module. **If a future
region change is considered, check each resource type's actual supported-region
list first** — Azure resource types don't all support the same regions, and
this won't be the last one to have a short list.

### 2026-07-09 — westeurope itself was (temporarily) rejecting new resources
The `staticWebAppLocation: westeurope` fix above compiled fine but then hit
a *second*, different failure on the next deploy: `RequestDisallowedByAzure`
— "The selected region is currently not accepting new customers" (see
`aka.ms/locationineligible`). This is an Azure-wide capacity restriction on
the region itself, not a subscription-specific trust/verification issue and
not something a code fix resolves — confirmed with the user before acting,
since EU-only hosting (`westeurope` was the only EU option in Static Web
Apps' short region list) vs. immediate availability is a real tradeoff, not
a bug. Decision: switch to `eastus2` now to unblock Tier 0 testing, revisit
before public launch. Only the build/API service's location is affected;
served static assets are behind a global CDN regardless, and this resource
never stores personal data itself (Supabase does, unaffected by this
choice) — judged not to need a `docs/legal/*.md` update on that basis, but
worth re-checking if that reasoning ever stops holding (e.g. if a future
change adds server-side rendering or logging to this resource).

### 2026-07-09 — Bicep decorator syntax errors blocked deploy-infra
Once the lowercase image-tag bug was fixed, `deploy-infra` reached the
actual `az deployment group create` step for the first time and failed
compiling `backend-container-app.bicep` with BCP071/BCP236/BCP166 errors.
Two distinct causes, both in that file:
1. **New, from S-002**: `corsAllowedOrigin`'s `@description(...)` used `''`
   to escape an apostrophe (`App''s hostname`) — that's SQL/Pascal-string
   escaping, not Bicep's. Bicep escapes a literal single quote as `\'`
   inside a single-quoted string. The `''` was silently accepted by every
   local review pass (no Bicep compiler available in this sandbox either —
   same network-policy wall as the missing dotnet SDK) because nothing
   actually parsed it until Azure's own Bicep compiler ran.
2. **Pre-existing, from S-001**: `minReplicas` had two stacked
   `@description(...)` decorators on one param — Bicep doesn't allow
   duplicate decorators of the same kind. Never caught because
   `deploy-infra` never reached the compile step before (blocked first by
   missing secrets, then by the lowercase image bug).
Fixed by escaping the apostrophe correctly and merging the two `minReplicas`
descriptions into one. **If a future Bicep edit needs a literal apostrophe
in a description string, use `\'`, not `''`.** Also worth noting: this
sandbox has no Bicep/az CLI to validate `.bicep` syntax locally, same
limitation as the backend C# — `deploy.yml`'s actual run against Azure is
the only real compile check available in this environment.

### 2026-07-09 — `dotnet run`'s launch profile overrides `ASPNETCORE_URLS` (S-002)
`ci.yml`'s e2e-tests job set `ASPNETCORE_URLS: http://localhost:8080` as a
step env var, but the API still bound to `:5028` and the health-wait curl
loop timed out — confirmed via CI logs, not locally (see the next note).
Cause: `dotnet run` without `--no-launch-profile` reads
`Properties/launchSettings.json`'s `applicationUrl` and uses that in
preference to an externally-set `ASPNETCORE_URLS`, even though the env var
is already present in the process environment before `dotnet run` starts.
Fixed by adding `--no-launch-profile` to the "Start API" step's `dotnet run`
command. If a future workflow step starts the API via `dotnet run` and sets
`ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS` to pick the port, add
`--no-launch-profile` there too — this isn't a one-off, it'll bite any
`dotnet run` invocation that also sets the port via env var.

### 2026-07-09 — `deploy.yml`'s image tag broke on the repo's mixed-case name
First real run of `deploy.yml` against actual Azure secrets (after PR #8/
S-002 merged to `main`) failed immediately in `build-and-push-backend`:
`docker build` rejected `ghcr.io/johanpearson/xG-Arcade-api:<sha>` with
"repository name must be lowercase". `${{ github.repository }}` returns the
repo's actual-case name (`xG-Arcade`, capital G/A) — GHCR/Docker image names
must be all-lowercase. Present since S-001 first wrote this workflow but
never caught, since this was the first run with real secrets (github.com
push-triggered deploy.yml runs on public PRs always execute regardless of
secrets, but silently no-op/fail early on missing Azure creds before
reaching the build step — so this specific failure was invisible until
Azure OIDC secrets existed). Fixed by lowercasing a copy of
`github.repository` before building the tag. **If a future workflow
composes a GHCR/Docker tag from `github.repository` directly, lowercase it
first** — the repo name will very likely never change case, but nothing
stops a future repo rename or a copy-paste into a new workflow from hitting
this again.

### 2026-07-09 — `deploy-frontend`'s missing deployment_token was expected, not a bug
Same `deploy.yml` run also failed `deploy-frontend` with "deployment_token
was not provided" — this is correct, not a regression: `DEV_AZURE_STATIC_WEB_APPS_API_TOKEN`
is a post-first-deploy secret (`infra/README.md`), and the Static Web App
resource it belongs to doesn't exist until `deploy-infra` succeeds at least
once — which itself was blocked by the lowercase bug above. Once
`deploy-infra` runs successfully, grab the token + `DEV_BACKEND_HOSTNAME`/
`DEV_FRONTEND_HOSTNAME` from the new Azure resources and set them as
secrets — `deploy-frontend` will keep failing on every run until then, by
design, not by accident.

### 2026-07-09 — dotnet SDK install: `dotnet-install.sh`/Microsoft's CDN is blocked, `apt` works (correcting an earlier S-002 note)
S-002's session hit `builds.dotnet.microsoft.com` returning a 403 policy
denial via the agent proxy and concluded the SDK couldn't be installed at
all in this sandbox, leaving backend changes locally uncompiled that
session. That conclusion was too broad: `dotnet-install.sh` and every
Microsoft CDN host tried (`dotnetcli.azureedge.net`,
`download.visualstudio.microsoft.com`, `dotnetcli.blob.core.windows.net`)
are indeed blocked, but Ubuntu's own apt repositories are not — `sudo
apt-get update && sudo apt-get install -y dotnet-sdk-10.0` installs .NET 10
cleanly (installs to `/usr/lib/dotnet`, `dotnet ef` needs `export
PATH="$PATH:/root/.dotnet/tools"` after `dotnet tool install --global
dotnet-ef`). `nuget.org` itself was already known-reachable; this just
closes the gap on the SDK itself. **A future session in this sandbox should
try `apt-get install dotnet-sdk-10.0` before assuming the SDK is
unavailable** — S-004 (this story) built, tested, and locally ran the API
end-to-end this way, including exercising it live with `curl`.

### 2026-07-09 — EF Core `UseInMemoryDatabase` inside a WebApplicationFactory `AddDbContext` lambda needs the db name captured outside the lambda (S-004)
`AddDbContext<T>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))`
looks like "one fixed database name for the test," but the configure lambda
runs fresh every time a new scope builds `DbContextOptions<T>` — so a
request's own DI scope and a test's own `factory.Services.CreateScope()`
each got a *different* random database name, and data written by one was
invisible to the other (test assertions saw `null` immediately after a 201
response confirmed the write happened). Fix: capture the name in a local
variable *before* the lambda (`var dbName = Guid.NewGuid().ToString();`
then reference `dbName` inside), so every scope shares it. Also, simply
`services.RemoveAll<DbContextOptions<XGArcadeDbContext>>()` +
`RemoveAll<XGArcadeDbContext>()` is not enough to swap providers this way —
`AddDbContext` also registers an internal `IDbContextOptionsConfiguration<T>`
descriptor holding the *original* `UseNpgsql(...)` action, which survives
and gets applied alongside the new `UseInMemoryDatabase(...)` action,
producing "Only a single database provider can be registered." That
internal type isn't ref-assembly-visible (`CS0234` if referenced directly),
so filter and remove every service descriptor closed over the DbContext
type by reflection instead (see
`backend/tests/XGArcade.Api.Tests/AuthEndpointTests.cs`'s `SetUp`) rather
than naming the internal type. **Any future WebApplicationFactory test that
swaps `XGArcadeDbContext` for an in-memory provider should copy that
pattern**, not just the two `RemoveAll<T>()` calls Microsoft's own basic
docs example shows.

### 2026-07-09 — ASP.NET Core JwtBearer remaps `sub`/`role` claims to long XML-Soap URIs unless `MapInboundClaims = false` (S-004)
A JWT with a `"sub": "<guid>"` claim validated successfully (JwtBearer log:
"Successfully validated the token"), but `User.FindFirstValue("sub")` in
the controller returned `null` — the claim's `Type` had been silently
rewritten to `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`
(and `"role"` to a similar long URI) by `JwtBearerOptions`' legacy inbound
claim-type mapping, which is still on by default even with the newer
`JsonWebTokenHandler`. Set `options.MapInboundClaims = false;` in
`AddJwtBearer(...)` to keep claims exactly as issued — needed here so
`ClaimsPrincipalExtensions.GetAuthProviderUserId()` can look up `"sub"`
literally, and so a future admin-authorization check can look up `"role"`
literally too, matching Supabase's own claim names instead of .NET's
legacy remap.

### 2026-07-09 — ci.yml's `Auth__Mode: "local-e2e"` (added ahead of time by S-002) is what S-004 actually implements against
`ci.yml`'s `e2e-tests` job already carried `Auth__Mode: "local-e2e"` with a
comment ("bypasses Supabase JWT validation with a local test signer —
never enabled outside Development") before S-004 existed — S-002
anticipated this need but left the actual mechanism unbuilt. S-004 builds
it for real: `Program.cs` checks `Auth:Mode == "local-e2e" &&
IsDevelopment()` and, only then, swaps in `LocalE2EAuthClient` (fakes
Supabase signup/login, mints a locally HS256-signed JWT, no real password
check) instead of the real `SupabaseAuthClient`. This is also why
backend-mediated signup (ADR-0013) was workable at all for CI: `ci.yml` has
no live Supabase project and never will for Tier 0's local-stack E2E run.
**If a future story (S-010's login E2E test, most likely) needs to log a
real account in during CI, this is already wired — call `/auth/login` with
any email/password against the local stack, no Supabase secrets needed.**

### 2026-07-09 — `deploy.yml`'s three latest runs all failed on unconfigured/misformatted dev secrets, not code bugs
Investigated runs #15 (S-004), #16 (ci fix), #17 (S-005) — all `failure`.
Two distinct root causes, both secret-configuration, not application code:
1. **`deploy-infra` (runs #15, #16):** Azure rejected the ARM deployment —
   `ContainerAppSecretInvalid: Container app secret(s) with name(s)
   'supabase-anon-key' are invalid: value or keyVaultUrl and identity
   should be provided`. Cause: `DEV_SUPABASE_ANON_KEY` is empty/unset, so
   `backend-container-app.bicep` tries to create a Container App secret
   with an empty value, which Azure's Container Apps API rejects outright.
   Not fixable by making the Bicep tolerate a blank value here — `Program.cs`
   requires `Supabase:AnonKey` unconditionally outside `Auth:Mode=local-e2e`
   (ADR-0013), so a blank-tolerant Bicep would only trade this clear,
   fail-fast deploy-time error for a silent container crash-loop at
   runtime. The actual fix is setting a real `DEV_SUPABASE_ANON_KEY` value
   (Supabase dashboard → Settings → API → anon/public key).
2. **`migrate-and-seed-database` (run #17, first run of this new S-005
   job):** `Npgsql.NpgsqlConnectionStringBuilder` threw `ArgumentException:
   Format of the initialization string does not conform to specification
   starting at index 0` while EF Core's migrator tried to open the
   connection. `ConnectionStrings__Database` comes straight from
   `DEV_DATABASE_CONNECTION_STRING` via `AddEnvironmentVariables()` with no
   other config source in play, so the string itself is malformed. Most
   likely cause: Supabase's dashboard defaults to showing the connection
   string in **URI** form (`postgresql://user:pass@host:port/db`), which
   Npgsql's ADO.NET-style keyword=value parser can't read — it needs the
   dashboard's **.NET** tab format instead (`Host=...;Username=...;
   Password=...;Database=...`). This had been silently latent since S-002's
   first deploy: `deploy-infra` only ever passed this string through to
   Azure as an opaque secret value (no parsing), so it never got exercised
   by anything that actually opens an Npgsql connection with it until this
   job. **Any Supabase Postgres connection string secret must be saved in
   .NET/ADO.NET format, never the URI form the dashboard defaults to** —
   `SETUP.md` and `infra/README.md` updated to call this out explicitly.
   Neither secret's actual value is visible to fix directly; both need the
   repo owner to update them in GitHub Actions secrets.

### 2026-07-09 — `deploy-infra`'s unquoted `--parameters` interpolation broke on the real (semicolon-bearing) connection string
Once `DEV_DATABASE_CONNECTION_STRING` was corrected to the real .NET/ADO.NET
format (see the note above), `deploy-infra` hit a *new*, different failure:
`ERROR: Missing input parameters: supabaseAnonKey, supabaseJwtSecret,
supabaseUrl` — even though the job log showed all three masked as `***`
(present, non-empty). Root cause: `deploy.yml`'s `az deployment group
create` step interpolates `${{ secrets.X }}` directly into an unquoted
`key=${{ secrets.X }}` shell token. A `.NET`-format connection string always
contains `;` (and usually a space, e.g. `SSL Mode=Require`) — unquoted `;`
is a bash command separator regardless of surrounding whitespace, so once
GitHub Actions substituted the real value into the script, bash silently
split the *one* `az deployment group create ...` invocation into several
bogus commands at each `;`. The connection string itself got truncated at
its first `;`, and every `--parameters` entry written after it in the
source (`supabaseJwtSecret`, `supabaseUrl`, `supabaseAnonKey`) never reached
`az` at all — they'd been shell-parsed as arguments to unrelated
non-existent commands instead. Not visible with the earlier (broken, URI-form)
connection string because that value happened to contain no `;`.
Fixed by quoting every interpolated value in the `--parameters` line
(`databaseConnectionString="${{ secrets.DEV_DATABASE_CONNECTION_STRING }}"`,
etc.) — also fixed the same unquoted pattern in `infra/README.md`'s and
`SETUP.md`'s manual-deploy command examples. **Any future workflow step
that interpolates a GitHub secret directly into a shell command must quote
it**, even if today's value happens not to contain a shell metacharacter —
`ConnectionStrings`/passwords/tokens can gain one at any time and the
failure mode (partial command truncation, wrong parameters silently
missing) is much harder to diagnose than an empty-value error.

### 2026-07-09 — the deployed dev Container App never sets `ASPNETCORE_ENVIRONMENT` (found via S-004's architecture review, not yet fixed)
Neither `infra/bicep/modules/backend-container-app.bicep` nor
`.github/workflows/deploy.yml` sets `ASPNETCORE_ENVIRONMENT` for the
Container App itself — only `ci.yml`'s local-stack `e2e-tests` job sets it
(to `Development`, for a process it starts directly). ASP.NET Core defaults
to `Production` when the variable is absent, so the *deployed* dev
Container App is actually running as `Production` right now, despite
`architecture-document.md` §9 describing dev as
`ASPNETCORE_ENVIRONMENT != Production`. Harmless today (nothing yet checks
the environment there), but COMP-09's `Testing.SeedManager` (ADR-0006,
gated on `!= Production`) and S-004's own `Auth:Mode=local-e2e` gate (on
`IsDevelopment()`) will both silently stay inactive in the deployed dev
environment once built, if this isn't fixed first. **Whichever story wires
up COMP-09 (S-005 or later) should add `ASPNETCORE_ENVIRONMENT=dev` (or
similar non-Production value) to the Container App's env vars in
`backend-container-app.bicep` before relying on that gate there.**

### 2026-07-09 — this sandbox's outbound network policy blocks `wikidata.org` entirely (S-006)
`curl https://www.wikidata.org/...` and `WebFetch` against any
`wikidata.org`/`www.wikidata.org` URL both fail with a 403 at the agent
proxy's CONNECT step (`gateway answered 403 to CONNECT` — see
`$HTTPS_PROXY/__agentproxy/status`'s `recentRelayFailures`), independent of
any code or Wikidata-side issue. This blocked S-006's backlog-mandated
manual check ("verify at least 2-3 seeded clubs' QIDs point at the
senior/first-team item, not a generic club concept... this can't be
unit-tested, it's a data-curation check against real Wikidata pages") —
could not be performed from this session. Unlike `query.wikidata.org` (the
actual SPARQL endpoint `WikidataClient` calls, exercised only via mocked
HTTP in tests here, never a real network call from this sandbox), the plain
`www.wikidata.org` article pages used for this specific manual check are
apparently not allowlisted. **Whoever next has real Wikidata access should
do this spot-check before/soon after merging S-006** — the QIDs themselves
were already verified correct (2026-07-08, see CHANGELOG) for
country/club identity, just not yet for the senior-vs-youth-academy
distinction implementation-document.md §6a flags as a known residual gap.

### 2026-07-10 — RoundDuration must be >= the longest gap between generate-round.yml's cron firings, not just "roughly matching" (S-008)
REQ-301's "one round ahead" rule is an idempotency check (skip generation
if an upcoming round already exists), not a counter — which made it easy
to assume any RoundDuration "close enough" to generate-round.yml's cron
cadence would work. It doesn't. Traced through by hand: `generate-round.yml`'s
`0 6 * * 2,5` (Tue+Fri 06:00 UTC) has *unequal* gaps — Tue->Fri is 3 days,
Fri->Tue is 4 days, since 7 isn't evenly divisible by 2. With
RoundDuration=3 days (matching only the shorter gap), simulating the chain
by hand shows a round closes a full day before the next cron fire ever
generates its successor (e.g. a round ending day 6 with the next cron fire
not until day 7) — a real, recurring gap, not a one-off. Setting
RoundDuration to the *longer* gap (4 days) instead fixes it: each new
round's StartTime chains from the previous round's fixed EndTime (not from
"now" when cron fires), and since 4 days always exceeds the cron's actual
firing interval (average ~3.5 days), the chain's end times grow faster than
real time passes, so a cron firing always finds the latest round already
active or still upcoming, never fully closed. Generation sometimes runs
"early" (round N+1 created while N still has a day or two left) — that's
REQ-301's intended behavior, not a bug. **Rule of thumb**: RoundDuration
must be >= max(gaps between consecutive cron firings), never just an
average or a rough match — change `RoundSchedulingOptions.RoundDuration`
(`XGArcade.Api/Program.cs`) and `generate-round.yml`'s cron together, never
independently.

### 2026-07-10 — Supabase's JWT Signing Keys mean no single static secret validates production tokens (ADR-0017)
Manually testing the deployed dev environment after S-010 (login succeeded
per Supabase, but every following request 401'd and silently bounced back
to the login screen) took a long live-debugging loop to pin down, mostly
because the deployed default logging (`Microsoft.AspNetCore: Warning`)
suppresses the JWT middleware's own failure logging — nothing showed up in
the log stream at all until `Logging__LogLevel__Microsoft.AspNetCore=Information`
was added as a temporary Container App env var (Azure Portal only, no
redeploy) specifically to see past it. Once visible, the real error was
`IDX10503: Signature validation failed... Number of keys in Configuration:
'0'` — the token's `kid` header claim is the tell: this Supabase project
signs with its newer asymmetric JWT Signing Keys (rotating keys, verified
via a JWKS endpoint), not the static HS256 shared secret `Program.cs`
assumed. **If a similar "logged in but every next request 401s" report
comes up again**: check for a `kid` claim in the rejected token before
assuming a copy-pasted secret is wrong — re-copying the same kind of secret
will never fix a structural algorithm mismatch, and cost real time before
this was recognized. `Auth:SupabaseJwtSecret` is gone now (ADR-0017); don't
reintroduce it.

Also worth remembering: `OpenIdConnectConfiguration.JsonWebKeySet`'s setter
does **not** auto-populate `.SigningKeys` (Microsoft.IdentityModel.Protocols
.OpenIdConnect 8.0.1) — that's not documented anywhere obvious, just
confirmed by writing a unit test that failed with 0 keys until
`.SigningKeys` was populated explicitly from `JsonWebKeySet.GetSigningKeys()`.
Easy to get this wrong again if this code is ever rewritten from memory
instead of copied.

### 2026-07-11 — `play-grid.spec.ts` had never actually run against a real `WikidataClient` until S-013 (E2E timeouts sized for the wrong latency budget)

Running the full local-stack E2E suite for real during S-013 (this sandbox
has no Docker daemon, so Postgres 16 was started directly via
`pg_ctlcluster 16 main start` instead of `ci.yml`'s service container — same
schema/seed either way, `dotnet run ... migrate-and-seed` doesn't care) hit
a real, previously-unverified failure: the "two wrong guesses" test's
dialog-close assertion timed out at the default 5s. Backend unit tests
mock `IWikidataLookupService`, so this was the first time this spec ever
exercised a *real* `WikidataClient` making a *real* HTTP call — and
ADR-0018 (REQ-211, merged after this spec was last touched) means every
guess that misses cache now re-runs the cell's Wikidata query before the
guess response returns. Confirmed directly with timed `curl` calls against
the running API: a wrong guess took anywhere from 0.4s to 6s in this
sandbox (`query.wikidata.org` is proxy-blocked here — sometimes a fast
403, sometimes a slower "connection reset by peer" — so the actual cost
observed is proxy-failure latency, not real Wikidata query time), not a
hang or a deadlock. ADR-0011 already documents that a *real* reachable
WDQS query can itself take 9-27s under load, and `WikidataClient`'s own
timeout is 15s — so even against a real, working Wikidata endpoint, this
test's 5s assumption was already wrong before this sandbox's specific
network policy ever entered the picture. Fixed by widening only the
assertions that follow a cache-missing guess to a 20s timeout and giving
the whole spec file a 60s per-test timeout, rather than either loosening
the global Playwright config (would mask genuinely-slow *other*
assertions) or changing `GridGameModule`'s already-accepted ADR-0018
behavior (revisiting a settled, reviewed decision is out of scope for a
QA pass). **If a future story adds another E2E test that submits a guess
that might miss cache, budget for ADR-0018's live-lookup cost explicitly**
— don't assume the old cache-only latency still applies just because
earlier specs got away with the default timeout (they did, but only by
variously getting lucky or exercising cache hits, not because the
assumption is actually safe). Separately, this also means every
genuinely-wrong guess in *production* now costs one live Wikidata call
before the player sees "incorrect" — ADR-0018's own "Consequences" section
already accepted this trade-off, but didn't call out the concrete
worst-case player-facing latency (up to ~27s per ADR-0011's own evidence);
worth watching once real usage exists, and exactly the kind of thing
ADR-0018's own "Follow-up" note (add `PlayerNameIndex` as a pre-filter
purely for latency, if it's ever built) was anticipating.

### 2026-07-11 — `accent-gold`/`accent-green` both fail WCAG contrast as text/icon color; darkened variants added (S-013)

`design-document.md` §6 had flagged this as unverified since the doc's
v0.2 rewrite ("gold in particular can run light-on-light if not
deliberately darkened for text use") — S-013 finally computed it. WCAG
relative-luminance contrast against `surface-card`/`#FFFFFF`:
`accent-gold` (`#C99A2E`) ≈ 2.6:1 (fails even the 3:1 large-text/icon
floor — this was painting `CellState`'s correct-cell checkmark and meta
text directly), `accent-green` (`#1E9E63`) ≈ 3.4:1 (fails the 4.5:1
normal-text floor — this was the white-on-green submit-button label color
pair in both `GuessInput.css` and `AuthScreen.css`, and the leaderboard
"you" tag's text color). `accent-red` (`#C4463C`) was already fine
(≈4.9:1), no change needed. Added two darkened, same-hue tokens rather
than editing the originals in place — `accent-green`/`accent-gold` still
have real non-text uses (live-dot, focus ring, tab underline) that
already clear the non-text 3:1 floor at their original, more saturated
values, and darkening those in place would have been an unrequested
visual change to things that were never broken. `accent-gold-text`
(`#8D6C20`) ≈ 4.9:1, `accent-green-text` (`#187E4F`) ≈ 5.1:1 — both
computed by scaling the original RGB toward black (preserves hue) until
crossing 4.5:1 with a small margin, not picked by eye. **If a future
screen needs gold or green as a text/icon/button-label color, use the
`-text` variant, not the base token** — this project's contrast floor
(design-document.md §6) treats this as load-bearing, not a nice-to-have.

The exact JWKS path (`/auth/v1/.well-known/jwks.json`) was never verified
against a live Supabase project from a dev sandbox — this sandbox's network
policy blocks Supabase's API the same way it blocks `wikidata.org` (see the
2026-07-09 entry above). It's Supabase's documented path for this system,
and the fix's own startup log line (`Program.cs`) announces the resolved
address so this is confirmable in one log-stream check on the next real
deploy — but if a future session finds it wrong, that's expected to have
needed live confirmation, not a sign the fix itself was rushed.

### 2026-07-12 — "latest round" is never the round that needs closing (ADR-0022)

Non-obvious enough to be worth writing down: when wiring round-closing
into `RoundGenerationService`, the tempting first instinct is "if
`latest.EndTime <= now`, close it." That's wrong. REQ-301's "one round
ahead" design means a round is generated — and becomes `GetLatestByGameKeyAsync`'s
answer — the moment its *predecessor* has merely started, not once the
predecessor has ended. So by the time a round's own `EndTime` actually
passes, it has long since stopped being `latest`; something newer already
took over that title. Checking `latest.EndTime` will (almost) never fire.
The round that actually needs closing at any given `generate-round.yml`
tick is `latest`'s *predecessor* — `IRoundRepository.GetPreviousByGameKeyAsync`
exists specifically to fetch it. Worked through by hand with concrete
timelines before committing (no `dotnet` SDK in this sandbox to verify by
running it) — if this ever gets refactored, re-derive the timeline rather
than trusting intuition about which round is "latest" at close time.

### 2026-07-12 — a manual `generate-round.yml` dispatch 500'd with zero diagnostic detail; fixed the visibility gap, root cause still unconfirmed

A manual `workflow_dispatch` of `generate-round.yml` (run #29205140633,
after the Tue 07-10 scheduled run had already succeeded once) failed both
attempts it was given — attempt 1 in ~11s, attempt 2 (the one reported)
in ~30s, both a bare HTTP 500 with `curl -f` swallowing whatever body came
back. Root cause is still **not confirmed** — nothing in this sandbox can
reach the real dev Container App's log stream — but the code review that
followed found two real, independent problems worth fixing regardless of
what actually happened this time:

1. `InternalRoundEndpoints.cs`'s `/internal/generate-round` handler only
   ever caught `GridGenerationException` — any other failure (a DB blip,
   an unswallowed HTTP exception, ...) fell through to ASP.NET's default
   *empty* 500, indistinguishable from every other failure mode in the
   workflow's own log. `GridTemplateResolver.GetOrCreateBySizeAsync` was
   also being called *outside* the try block entirely, so a failure there
   specifically had no chance of ever getting a problem-details response.
   Fixed: the whole handler body now runs inside the try, with a
   catch-all `Exception` branch added alongside the existing
   `GridGenerationException` one — both log full detail server-side and
   return it as `Results.Problem()`'s `detail`, matching this endpoint's
   sole caller being the bearer-token-gated scheduler job, never a public
   client (architecture-document.md §7's client-appropriate-summary rule
   is aimed at public endpoints, not this one).
2. `generate-round.yml` used `curl -f`, which discards the response body
   on any non-2xx status — even after fix (1) started returning a real
   problem-details body, the workflow itself would still have hidden it.
   Switched to capturing the body with `-o`/`-w "%{http_code}"` and
   printing it before failing the step on a >=400 status.

Separately clarified for whoever reads this next: `GridGameModule`'s
column-candidate retry (`PickColumnHeadersAsync`) rejects and replaces a
**whole club candidate** the moment it fails against *any* fixed row
header — it does not retry a single (row, club) cell in isolation, and
the row headers themselves are picked once and never reshuffled. So one
weak cell doesn't cause a failure by itself, but a row country that pairs
poorly against the whole 15-club Tier 0 reference list can burn through
every remaining candidate and abort the entire grid (and thus the whole
`/internal/generate-round` request) with no fallback. Worth knowing if
this recurs with a specific country implicated. **Next time this fires
for real, check the Container App log stream for the actual exception
before assuming it's this** — the fix above makes the failure visible
from the workflow log itself, which should make that check unnecessary
going forward.

### 2026-07-13 — the predicted failure mode actually happened, twice, in sequence

Confirms the prediction two entries above almost exactly. After S-035's
`MaxDuration` fix merged, the very next `generate-round.yml` dispatch (real
`main`, real deploy) failed in two different ways back to back:

1. First dispatch (right after merging PR #43+#44 close together): HTTP
   503 `"no healthy upstream"` — a deploy race, not a real bug. The manual
   dispatch landed mid-rollout of the deploy triggered by the merge itself;
   irrelevant to anything below, recorded only so a future "why did it fail
   right after I merged" doesn't re-diagnose this from scratch.
2. Next real dispatch, once the deploy had settled: HTTP 504 `"stream
   timeout"` after exactly 240s — `PickHeadersAsync` had chained enough
   live Wikidata lookups to blow past Azure's ingress timeout. This is
   S-035's whole reason for existing, and it's what motivated `MaxDuration`.
3. **After merging S-035's fix**, the next dispatch failed fast (not slow)
   with `GridGenerationException: "Ran out of candidates before completing
   the grid."` — the *other* half of the same underlying problem, and
   exactly what the 2026-07-12 entry above predicted almost word for word:
   "a row country that pairs poorly against the whole 15-club Tier 0
   reference list can burn through every remaining candidate and abort the
   entire grid." `MinValidAnswers=5` (S-014) against only 15 clubs means a
   lot of real country/club pairs, especially smaller-market countries,
   genuinely have fewer than 5 shared historical players — no amount of
   retrying fixes that, since it isn't bad luck, it's the reference data
   itself. It failed *fast* this time (not another 4-minute hang) because
   most of the 15-club pool was already cached at 0 matches from earlier
   failed attempts — a cache hit, not a fresh Wikidata timeout.

Fixed by S-036: a proactive `PlayerCacheWarmingService` (`dotnet run --
warm-player-cache`, run manually via `warm-player-cache.yml`, deliberately
a CLI verb and not an HTTP endpoint — see that story's own "Built as" note
for why both an endpoint and a fire-and-forget background task are unsafe
for this specific hosting setup) plus a widened reference pool (20→45
countries, 15→21 clubs). **The cache-warming job alone does not raise the
success rate** — it only makes each individual pair's cached-vs-uncached
status resolve fast instead of slow. The reference-pool widening is what
actually raises the odds a random row-header pick has enough valid
columns to work with. Both were asked for together and shipped together;
worth remembering they solve different halves of the problem if either
one alone doesn't fully fix future failures.

**If this happens again**, the two most useful diagnostics are: (1) did it
fail fast or slow — fast means "ran out of candidates" (a data-sparsity
problem, needs more/better reference data or a lower `MinValidAnswers`),
slow-then-`MaxDuration` means something is still forcing a lot of live
Wikidata calls (check whether `warm-player-cache` has actually been run
since the last reference-data change); (2) run `warm-player-cache.yml`
again — new reference-data entries or newly-synced Wikidata content since
the last warming pass are the most likely explanation for a fresh
data-sparsity failure.

### 2026-07-13 — 4 of S-036's hand-guessed club QIDs were wrong; caught only by manual verification, not by the system itself (S-037)

Asked-for follow-up after S-036 shipped: the user manually checked every
new QID against live Wikidata pages (something this sandbox can't do —
network policy blocks `wikidata.org`, same limitation recorded elsewhere
in this file) and found 4 of the 6 new club QIDs were wrong: Napoli
(`Q1176`→`Q2641`), AS Roma (`Q2483`→`Q2739`), Sevilla (`Q10360`→`Q10329`),
Porto (`Q182982`→`Q128446`). Worth internalizing why this class of bug is
genuinely dangerous rather than a minor data-entry slip: a wrong QID
doesn't fail loudly. `WikidataClient`'s SPARQL queries have no way to know
a QID doesn't correspond to the intended entity — if it happens to be some
*other* real Wikidata item that also satisfies the query shape (`?player
wdt:P106 wd:Q937857. ?player wdt:P54 wd:{{clubQid}}.`), the query returns
real players, persisted under the *intended* club's name, looking
completely normal. `ReferenceDataSeeder.cs`'s own S-036 comment predicted
exactly this ("a wrong QID here is self-limiting, not dangerous... just
return zero bindings") and that prediction was simply wrong for these 4 —
they weren't nonexistent QIDs returning nothing, they were *other real
entities* returning real-but-wrong data. **Don't repeat that reasoning
next time a QID goes unverified** — "probably safe because it'll just
return nothing if wrong" only holds for a QID that doesn't resolve to
anything at all, not for one that resolves to the wrong thing.

Two real gaps found and fixed while correcting the QIDs, both worth
remembering:

1. `ReferenceDataSeeder.SeedAsync` only ever *added* a club/country row by
   name, never corrected an existing one's `WikidataQid` if it changed —
   meaning simply editing the QID literals in this file would have done
   **nothing** against the already-seeded dev database (the wrong-QID rows
   were already there from S-036's deploy). Fixed: `SeedAsync` now updates
   an existing row's `WikidataQid` in place when it differs, keyed by the
   same by-`Name` lookup that already prevented duplicates. **If a QID
   ever needs correcting again, remember this only takes effect on the
   next `migrate-and-seed` run** (automatic on every `deploy.yml` push) —
   editing the source file alone changes nothing already deployed.
2. Even once the `ClubDefinition.WikidataQid` is corrected, whatever got
   persisted into `PlayerAttribute`/`PlayerData` under the wrong QID
   lingers forever with no way to tell it apart from correct data after
   the fact (no column records which QID a row was fetched under) — new
   `StaleClubAttributeCleaner` (`dotnet run -- clean-stale-club-attributes
   "Napoli,AS Roma,Sevilla,Porto"`, via `clean-stale-club-attributes.yml`)
   purges it so the next `warm-player-cache` run gets a clean re-fetch.
   **Run this manually, once, after `migrate-and-seed` has applied the QID
   correction and *before* the next `warm-player-cache` run** — running it
   after a fresh warm-player-cache pass would delete the new correct data
   too, since (again) nothing distinguishes old from new after the fact.

Also added 11 further clubs (RB Leipzig, Bayer Leverkusen, Marseille,
Lyon, Monaco, Lille, Lazio, Valencia, Real Sociedad, Newcastle United,
West Ham United) with QIDs the user verified directly this time, not
training-knowledge guesses — 21→32 clubs total.
