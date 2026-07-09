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
