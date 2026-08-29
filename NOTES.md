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

### 2026-08-18 — S-141: xG Path eligible-pool re-verification could not be run against real data in this sandbox; reset tooling built and handed off instead

S-141 (Epic 12, docs/backlog.md) asks for a before/after eligible-player
count for xG Path after S-137 (birth-year >= 1975 floor), S-138 (2 distinct
seeded clubs, up from 1), S-139 (B-team/reserve exclusion), and S-140
(regional/national regex fix) all landed together — confirmed merged to
`main` first (S-137 PR #212, S-138 PR #214, S-139 PR #215/216, S-140 PR
#217).

**The live count itself could not be produced here.** `XGPathGameModule.
GetEligiblePlayerIdsAsync`'s real pool is REQ-1201's structural checks
*narrowed by ADR-0056's `IPlayerFamiliarityService` live-Wikidata
familiarity filter* — not a DB-only query. This sandbox has no route to
either half of what "real (dev) data" would require: `query.wikidata.org`/
`www.wikidata.org` are blocked at the agent proxy (same wall documented
2026-07-09 and 2026-07-21 below), and there is no access to the real dev
Postgres instance carrying the ~608K-row `PlayerCareerStint` table
`prefetch-player-careers` has actually populated there. Computing a count
against a toy/empty local dataset would not be the number this story asks
for, so no number is fabricated here — same "flag for manual verification,
don't guess" discipline as the 2026-07-21 Supabase entry and the 2026-07-09
Wikidata entry below.

**What was built instead:** the one genuinely code-shaped piece of this
story — `reset-path-target-cycle`, a new `dotnet run --` CLI verb
(`backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs`,
`backend/src/XGArcade.Data/Seeding/PathTargetCycleResetter.cs`, tests in
`backend/tests/XGArcade.Data.Tests/PathTargetCycleResetterTests.cs`).
`PathTargetCycle.ObservedPoolSize` self-corrects for free on the next xG
Path generation, but `UsedInCycleCount`/`PathCycleTargetUsage` do not — they
were accumulated against the OLD, larger pre-S-137-140 pool. The new verb
wipes both tables (mirrors `PairLookupFailureCleaner`'s load-then-
`SaveChangesAsync` shape, safe/idempotent if xG Path has never generated a
round yet) so the next generation starts a clean `CycleNumber` 1 baseline
scored purely against the new pool.

**Handoff for whoever next has real dev access:** (1) hit `GET
/admin/xg-path/cycle` to record the current `ObservedPoolSize` as the
"before" figure (it still reflects the last generation under the OLD
rules until a new one runs); (2) trigger (or wait for) one real xG Path
round generation against the now-deployed S-137-140 rules — `POST
/internal/generate-round`'s scheduler path, or a manual dispatch; (3) hit
`GET /admin/xg-path/cycle` again for the "after" `ObservedPoolSize`; (4) run
`dotnet run -- reset-path-target-cycle` once, so `UsedInCycleCount` isn't
left mixing old- and new-rule selections; (5) record both numbers in a
follow-up NOTES.md entry — if the pool dropped by more than roughly half,
escalate to the product owner (widen the seeded club/country list via
`audit-club-gaps`, per S-141's own text) rather than silently accepting it.

### 2026-08-18 — S-151: DB-touching autocomplete warm-up built and tested; the actual dev-environment latency check is deferred to whoever has real Container Apps access

S-151 (Epic 13, `docs/backlog.md`) asks for two things: (1) a real,
DB-touching warm-up call (`GET /players/autocomplete/warmup`) fired
fire-and-forget on `GridScreen`/`PathScreen` mount, alongside the existing
app-load `/health` ping which only wakes the process and never touches
Postgres; and (2) manual verification against the deployed dev environment
that perceived first-keystroke latency actually drops.

**Part (1) is built and tested** — see the CHANGELOG entry dated today for
the full breakdown (files, tests, doc updates).

**Part (2) could not be done here**, same category of gap as S-141's
eligible-pool re-verification entry above: Container Apps' `minReplicas: 0`
scale-to-zero (`infra/bicep/modules/backend-container-app.bicep`) genuinely
scaling a replica down to zero and cold-starting on the next request is
real production/deployed-environment behavior — this local/CI sandbox never
scales to zero, so there's no cold path to measure a before/after against.
No number is fabricated here.

**Handoff for whoever next has real dev-environment access:** let the dev
Container App sit idle long enough to actually scale to zero (check current
`minReplicas`/idle-timeout in the Bicep module for how long that takes),
then time-to-first-suggestion (first autocomplete dropdown appearing after
typing >= 2 characters) on a cold hit — once against the current deployed
build (pre-S-151, `/health`-only warm-up) if a rollback/prior revision is
still reachable, then again post-S-151 (this warm-up landed). Record both
numbers here as a follow-up NOTES.md entry.

### 2026-08-03 — `PlayerCareerStint`'s "few thousand rows" full-table-read assumption is now stale (608K rows after ADR-0055)
Several existing in-memory-read helpers (`GetAllCareerStintsByPlayerAsync`,
`GetPlayersMissingPhotoAsync`, the new `GetUnseededClubCandidatesAsync`) are
documented as safe because of a "tolerate a full-table read at Tier 0's
player-pool scale (a few thousand rows)" precedent. That was true when
written, but ADR-0055's `prefetch-player-careers` job has since populated
`PlayerCareerStint` with 607,914 real rows (confirmed by its first clean
run) — two orders of magnitude past "a few thousand." Still fine for a
manual, occasional CLI job like `audit-club-gaps` (tens of MB, trivial for a
CI runner), but `GetAllCareerStintsByPlayerAsync` is on a hot path —
`XGPathGameModule`'s REQ-1201 eligibility check calls it on every round
generation, not just manually. Not fixed here (out of scope for this
session's work) — worth a dedicated look at whether that read still holds up
at real scale before it becomes a real production latency problem, rather
than waiting for a report the way ADR-0054's Celtic gap was.

**Update (2026-08-03, later same day):** the hot-path method flagged above
is fixed. `GetAllCareerStintsByPlayerAsync` is deleted; `XGPathGameModule.
GetEligiblePlayerIdsAsync` now reads in two passes — a new
`GetCareerStintCandidatePlayerIdsAsync` narrows the full player pool down to
real candidates using only a cheap `(PlayerId, ClubName)` projection (the
two necessary-but-not-sufficient conditions computable from that alone: >= 3
stint rows, and any stint at a seeded club), then `GetCareerStintsByPlayerIdsAsync`
(already existed) loads full stint data only for that narrowed set before
`IsEligible`'s existing checks run unchanged. Zero eligibility-semantics
change — the narrowing filter is a true superset, never excluding a player
`IsEligible` would have accepted. `GetPlayersMissingPhotoAsync` and the
general "few thousand rows" assumption elsewhere are still unaddressed —
this fix only covered the one hot-path method called out above.

### 2026-08-02 — `prefetch-player-careers`'s first real run: huge success overall, 4 batches hit the same 15s-default-timeout mistake `import-player-name-index` already made once

First-ever real run (ADR-0055, right after merging PR #140) processed
essentially the entire seeded country list — 49 countries, 177,872 players
touched, 607,914 stints added — in ~42 minutes, comfortably inside the
90-minute budget. `QueryPlayerPoolByNationalityAsync` (the per-country pool
query) never failed once, including for huge pools (United Kingdom: 18,460
players; Brazil: 10,949; Germany: 10,128) — so ADR-0055's own flagged risk
("a large country might exceed WDQS's ~60s server-side cap") did NOT
materialize. The job still went red at the end, though: 4 of the many
200-player `QueryPlayerCareerStintsByQidsAsync` career-fetch batches (mixed
in among Brazil's and Russia's batches) hit `WikidataClient`'s 15s default
timeout and failed — recoverable, isolated batch failures, not country
failures, exactly as designed (the fail-loud-at-end contract worked
correctly: kept going, reported the 4 failures, preserved everything that
succeeded, exited nonzero to signal "re-run to fill the gaps").

Root cause is the *exact same class of bug* as the
2026-07-17 `import-player-name-index` timeout entry below: 15s
(`WikidataClient`'s default, tuned in ADR-0011 for the narrow per-cell
intersection queries) is too tight for a 200-QID VALUES-clause query
fetching full P54 career histories — a genuinely heavier query shape than
what 15s was ever tuned for, not a sign of the server-side ~60s cap being
hit (this is the important distinction: don't reach for "slice the query
further" here, that lesson applies to the *other* failure mode). Fixed the
same way `import-player-name-index` was fixed for the identical reason:
gave `prefetch-player-careers`'s own standalone `WikidataClient` a 60s
`queryTimeout` override in `Program.cs`. **Lesson worth generalizing:** any
new by-QID batch query added to `WikidataClient` in the future should
default its CLI verb's standalone client to 60s from the start, not wait to
rediscover this the same way twice.

### 2026-08-02 — two `workflow_dispatch` jobs need a manual run after the diacritic-normalization/position-birthyear bug-bundle fix ships

Deploying the `PlayerNameNormalizer`/`Player.Position`/`Player.BirthYear` fixes
(non-decomposable-letter transliteration + the new
`backfill-player-position-birthyear` verb) fixes the *code*, but two pools of
already-persisted data need an operator to actually trigger a re-run before
players see the effect — neither happens automatically on deploy:

- **`import-player-name-index`** (`.github/workflows/import-player-name-index.yml`)
  — `PlayerNameIndex.NormalizedName` (autocomplete/COMP-10) is fully re-derived
  from source on every run of this importer, so a manual trigger after this
  fix deploys is sufficient to fix autocomplete suggestions for names
  containing Ø/Æ/Œ/Đ/Ł/ß/Þ (e.g. "Ødegaard") — no new backfiller was written
  for this table, deliberately, since the existing importer already does the
  job. `Player.NormalizedFullName`/`PlayerAlias.NormalizedAlias` (the
  correctness-side tables) DID get new/extended automatic backfillers wired
  into `migrate-and-seed`, so those fix themselves on the next push to main —
  only the autocomplete side needs this manual step.
- **`backfill-player-position-birthyear`** (new workflow, this same fix) —
  needs at least one manual run to clear the existing backlog (most `Player`
  rows predate the `Position`/`BirthYear` migration); `migrate-and-seed`
  deliberately does NOT call this automatically, same reasoning as
  `backfill-player-photos` never being wired into `migrate-and-seed` either
  (a live-Wikidata-call-per-batch job doesn't belong on every push-to-main
  deploy's critical path).

Worth remembering next time a bug report says "still broken after deploy" for
either of these — the deploy alone isn't enough, someone has to actually click
"Run workflow" once.

### 2026-07-27 — `backend/Dockerfile` needs a `COPY` line per project, and it's easy to forget

Two consecutive deploys (`deploy` runs #117, #118) failed at `dotnet publish`
with `NETSDK1004: Assets file '.../XGArcade.Games.XGPath/obj/project.assets.json'
not found`. Cause: `backend/Dockerfile` copies each project's `.csproj` file
individually (for Docker layer caching) before running `dotnet restore`, so
`dotnet restore` only sees projects that were explicitly `COPY`'d — it doesn't
walk the filesystem. When `XGArcade.Games.XGPath` was scaffolded (S-080) and
wired into `XGArcade.Api.csproj` as a `ProjectReference`, the Dockerfile
wasn't updated to `COPY` its `.csproj` too, so restore silently skipped it
("Skipping project ... because it was not found") and the later
`dotnet publish --no-restore` failed. Fixed by adding the missing `COPY`
line. Updated `.claude/agents/game-scaffolder.md` to call this out as a
required scaffolding step so the next new game module doesn't repeat it —
there's no build-time check that would otherwise catch a missing `COPY`
line, since `dotnet build`/`dotnet test` outside Docker restore the whole
solution and don't notice.

### 2026-07-25 — `AdminScreen.test.tsx`'s REQ-507 test is flaky under a full `npm run test` run

Found while quality-gating REQ-719 (unrelated diff). Failed 1 of 3 full-suite
runs (`Unable to find an element with the text: Total users` — the metrics
fetch hadn't resolved before the assertion) but passed 29/29 every time when
run in isolation (`npx vitest run src/admin/AdminScreen.test.tsx`). Smells
like a missing `await`/`findBy*` vs `getBy*` race, or cross-test fetch-mock/
timer leakage from another suite. Not a regression from any recent change —
worth a real look next time someone's in `AdminScreen.test.tsx`, rather than
re-discovering it from a random CI flake later.

### 2026-07-26 — S-076's `IScoringStrategy` extraction didn't reach `LiveRoundContributionService`

Found by `quality-architect` while gating S-076 (ADR-0040). `ScoreLockingService`
now resolves an `IScoringStrategy` per `Round.GameKey` instead of calling
`UniquenessCalculator`/`ScoringRules.PointsFromUniqueScore` directly — but
`LiveRoundContributionService` (the live per-cell/per-round contribution
formula behind `ILiveRoundContributionService`, ADR-0031) still calls both of
those directly, inline, unchanged. S-076's own scope (backlog.md) was
`ScoreLockingService` only, so this wasn't a defect in that story — but it
means ADR-0040's stated goal ("`Core.Scoring` gains zero compile-time
knowledge of any specific game") isn't fully true yet: once xG Path
(`ClueEfficiencyScoringStrategy`) ships, `LiveRoundContributionService` will
keep computing xG Grid's uniqueness formula for every game's live view,
producing wrong live points for xG Path rounds. Needs its own follow-up story
(resolve `IScoringStrategy` here too, same as `ScoreLockingService`) before
xG Path's frontend (S-085+) can rely on live points being correct — worth
raising before S-081-083 land, not after.

### 2026-07-25 — Sign-in latency follow-up: live evidence pointed at the client-side Turnstile step, not backend/cold start

Follow-up to the entry immediately below. The product owner manually
tested login repeatedly, back-to-back, and consistently saw an 8-12s
spinner right after clicking Login, every time — not a pattern that
fits "only the first request after idle is slow." They shared a real
Azure Container App log from one such attempt:

```
17:51:51.279 — POST https://<project>.supabase.co/auth/v1/token?* (start)
17:51:52.204 — response after 924.3ms — 200
17:51:52.236 → 17:51:52.362 — three fast EF Core queries (~125ms total):
  Login's own GetByAuthProviderUserIdAsync + UpdateLastActiveAtAsync
  (REQ-718, merged same day), likely immediately followed by the
  frontend's own GET /auth/me
17:51:52.362 → 17:51:58.189 — a 5.83s gap with ZERO server-side activity
  logged (no DB query, no outbound HTTP call), then one more identical
  user-lookup query
```

So the actual login request was fast end-to-end (~1.1s server-side, not
a multi-second cold call) — the felt delay had to be sitting somewhere
the backend logs can't see. Confirmed with the product owner that the
8-12s is the spinner between clicking Login and getting *any* result
back (not a slow page-load after a fast login) — which places nearly all
of it before `POST /auth/login` is even sent. `getTurnstileToken()`
(`frontend/src/lib/turnstile.ts`) only runs inside the submit handler,
never preloaded, so the whole chain (Cloudflare script download if
uncached, widget render, verification round-trip) was fully serialized
in front of the request. Device was normal mobile Chrome over home WiFi,
no reported ad-blocker/VPN, ruling out the most common "Turnstile is
blocked/slowed by a privacy tool" explanation.

Fix (see `infra/README.md`, `docs/decisions/0037-turnstile-captcha-for-guest-creation.md`'s
third amendment, and `docs/CHANGELOG.md` for the full write-up): preload
just the Turnstile *script* on screen mount (not the widget/token — those
still wait for submit, since a token is single-use and expires quickly),
and switch from invisible/managed mode to an always-visible checkbox —
the product owner's own call, since an invisible widget both hides that
anything is happening and can't fall back to an interactive challenge if
Cloudflare's risk scoring is ever unsure, which may itself have
contributed to the stuck-feeling attempts. **Also worth remembering:**
this is only a real fix if the Cloudflare Turnstile site itself is
configured as Managed/Non-Interactive mode in the dashboard — a site
created as Invisible mode cannot show a widget at all no matter what the
frontend's `size` parameter requests, since that's Cloudflare's own
server-side enforcement, not something client code can override. Check
this on any already-existing dev/prod Turnstile site before assuming the
code change alone fixed the visibility (see `SETUP.md` step 6's
correction).

### 2026-07-25 — Sign-in latency measured: cold start and captcha are additive, separate costs

Live report: sign-in feels slow, reportedly since the Cloudflare Turnstile
captcha rollout (ADR-0037). Rather than guess, measured real timing
against the deployed dev Container App via a temporary `workflow_dispatch`
diagnostic workflow (added, dispatched, then deleted — see
`infra/README.md`'s new "Sign-in latency" section for the numbers in
full and the resulting decision). This sandbox can't reach
`*.azurecontainerapps.io` directly (same proxy restriction as
`wikidata.org` elsewhere in this file), so the probe had to run from a
real GitHub Actions runner instead — and a brand-new `workflow_dispatch`
workflow can't be triggered via the API/UI until it exists on the
**default branch**, not just a feature branch (a genuine GitHub
limitation, not a permissions issue) — worth remembering next time a
diagnostic-only workflow is needed for a similar investigation.

Headline numbers: cold `/health` (first hit after ~22 idle minutes) took
9.93s, of which only 0.13s was the TCP connect — almost all of it was
Container Apps standing a replica up (`minReplicas: 0`, pre-existing
since ADR-0004/S-001, not caused by captcha). Warm `/health` settled at
~0.35s. The first `/auth/login` on an already-warm backend cost 1.97s —
about 1.6s more than warm baseline, the one-time cost of the backend's
first Supabase call (which now also asks Supabase to verify the captcha
token against Cloudflare) — dropping to ~0.45s on the next two attempts.
**Conclusion: cold start was always the bigger single cost and predates
captcha; captcha added a real but smaller tax on top, concentrated on the
first sign-in after any idle period** (both the measured
backend→Supabase→Cloudflare cost above, and an unmeasured-here but
structurally real frontend cost — `getTurnstileToken()`
(`frontend/src/lib/turnstile.ts`) makes the browser await a Cloudflare
token *before* `POST /auth/login` is ever sent, a genuinely new serial
delay stacked in front of everything else). Decision: keep
`minReplicas: 0` (this project is free-tier-only by explicit constraint;
raising it to 1 would cost ~$10-12/month) and document the trade-off
instead — see `infra/README.md`. Revisit if/when a real "prod"
environment exists and this stops being a solo-testing inconvenience.

### 2026-07-21 — Supabase Anonymous Sign-ins / user-update API shapes unverified (S-069, REQ-717)
`SupabaseAuthClient.SignInAnonymouslyAsync` (`POST auth/v1/signup` with no
email/password) and `LinkEmailPasswordAsync` (`PUT auth/v1/user`,
bearer-authenticated as the guest's own access token) were written from
Supabase's documented behavior, but neither was exercised against a real
Supabase project — this sandbox has no network access. Both response-shape
assumptions (anonymous signup returns the same session shape as a real
signup; user-update returns the updated user at the top level) reuse
existing, already-verified parsing code (`PostAuthRequestAsync`,
`SupabaseUser`), but the *request* shapes themselves are unverified. Same
class of risk as S-036/S-037's guessed Wikidata QIDs — flag for manual
verification against a real Supabase project before this reaches
production, don't assume correct just because it compiles/reads
plausibly.

### 2026-07-14 — EF Core's InMemory provider doesn't support ExecuteUpdate/ExecuteDelete (S-025)
Every repository test in this codebase runs against EF Core's InMemory
provider (`Microsoft.EntityFrameworkCore.InMemory`), which does not support
translating `ExecuteUpdateAsync`/`ExecuteDeleteAsync` — those only work
against a real relational provider (Npgsql/Postgres in this repo's case).
S-038's `purge-player-pool` CLI verb already used `Players.ExecuteDeleteAsync()`
but has zero test coverage, so this never surfaced before. S-025's new
`AnonymizeByUserIdAsync`/`RemoveMembershipsByUserIdAsync`/`User.DeleteAsync`
repository methods needed real unit test coverage (REQ-710), so they're all
written the ordinary load-then-`SaveChangesAsync` way through the change
tracker instead — same pattern every other repository method in this
codebase already uses. **If a future story wants a genuine bulk
Execute*Async for a large table** (justified performance reason, not just
convenience), it'll need either a real-Postgres-backed test (this repo has
none currently — see S-013's note on no Docker daemon in this sandbox) or
to accept that path stays untested, same as `purge-player-pool` today.

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
warm-player-cache`, run manually via `warm-grid-cache.yml`, deliberately
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
Wikidata calls (check whether `warm-grid-cache` has actually been run
since the last reference-data change); (2) run `warm-grid-cache.yml`
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
wdt:P106 wd:Q937857. ?player wdt:P54 wd:{{clubQid}}.` — **query shape now
stale**: since 2026-07-17 the P54 triple is the full statement path
`p:P54`/`ps:P54` excluding deprecated rank, see that day's entry below;
the wrong-QID lesson here is unchanged), the query returns
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
   purges it so the next `warm-grid-cache` run gets a clean re-fetch.
   **Run this manually, once, after `migrate-and-seed` has applied the QID
   correction and *before* the next `warm-grid-cache` run** — running it
   after a fresh warm-grid-cache pass would delete the new correct data
   too, since (again) nothing distinguishes old from new after the fact.

Also added 11 further clubs (RB Leipzig, Bayer Leverkusen, Marseille,
Lyon, Monaco, Lille, Lazio, Valencia, Real Sociedad, Newcastle United,
West Ham United) with QIDs the user verified directly this time, not
training-knowledge guesses — 21→32 clubs total.

### 2026-07-13 — Player pool had no gender or era restriction; whole pool purged and rebuilt (S-038, ADR-0025)

Separate follow-up the same day: the user flagged that the player pool
sourced from Wikidata should be restricted to male footballers, and to a
particular era — neither restriction existed before this. Unlike S-037's
wrong-QID fix, this isn't a bug in existing code (the queries worked
exactly as written), it's a scope decision about what the game should
cover at all.

Two things had to be made concrete: how to express "male" against
Wikidata's actual data model (`P21` = sex or gender, `Q6581097` = male —
there's no separate "male footballer" occupation item to filter on
instead), and where the era cutoff sits. **First pass got the second one
wrong**: implemented date of birth (`P569`) as a rolling "latest 100
years" window — `TimeProvider.GetUtcNow().AddYears(-100)`, computed fresh
on every query — before the user corrected course mid-review: the actual
requirement is a **fixed** date, players born in 1939 or later, not a
window that keeps sliding forward every year. Switched to a
`private const string` cutoff literal on `WikidataClient` and removed the
`TimeProvider` dependency entirely, since a fixed date needs no clock at
all. Date of birth over a career/active-period filter was still the right
call either way, since Wikidata's career-span data is far less
consistently populated than date of birth and would silently exclude real
in-scope players more often. Full reasoning, including alternatives
considered, is in ADR-0025 (rewritten in place to describe the fixed-date
decision, since this never shipped as the rolling version) — don't
re-litigate the date-of-birth-vs-career-span choice, or reintroduce a
rolling cutoff, without reading that first.

**The existing cached pool couldn't be selectively fixed.** Neither sex
nor date of birth was ever recorded on `Player`/`PlayerAttribute` rows, so
there was no way to tell which already-cached players would pass the new
filters without a live Wikidata re-check per player anyway — cheaper to
just purge everything and let `warm-grid-cache` re-fetch it all fresh
under the new query shape. New `purge-player-pool "delete all player
data"` CLI verb does the purge (`Player` row delete, cascading through
`PlayerData`/`PlayerOverride`/`PlayerAttribute`/`PlayerAlias`) — gated
behind an exact confirmation-phrase argument rather than a bare
non-blank check, since this is a bulk, unscoped delete (much larger blast
radius than S-037's per-club-name-scoped cleaner) — reused
`infra/scripts/promote-dev-to-prod.sh`'s existing `"promote to prod"`
confirmation-phrase pattern rather than inventing a new safety mechanism.

**What this does NOT touch:** `CountryDefinition`/`ClubDefinition`/
`TrophyDefinition` (reference tables), and `User`/`League`/`Round`/
`GridInstance`/`GridCell`/`Guess` (account/game-history data) — the user
explicitly scoped this to the player pool only. One consequence worth
remembering: `Guess.PlayerAnswerId` has no FK constraint on `Player` at
all (checked `XGArcadeDbContext.cs`'s `OnModelCreating` to confirm before
proceeding — if it did have a `Restrict`/no-action FK, the purge would
have failed outright with an FK violation the moment any purged player was
someone's historical guess answer). So an old `Guess` whose answer was a
since-purged player keeps its already-computed `IsCorrect`/score fine, it
just can't show which player that answer was anymore if anyone ever looks
back at that historical round.

**Operational sequence, in order, once this ships:** (1) deploy the code
change (new SPARQL filters), (2) trigger `purge-player-pool.yml` once with
confirmation phrase `delete all player data`, (3) trigger
`warm-grid-cache.yml` to repopulate the pool under the new filters. Step
2 before step 3, same reasoning as S-037's clean-then-warm ordering — the
purge has to happen before the pool is fresh, or it would just delete the
freshly-correct data too.

### 2026-07-17 — Truthy `wdt:P54` is best-rank-only; historical clubs silently vanished, tainting every seeded club's cache at once (REQ-113, REQ-111)

A genuinely correct guess (Sandro Tonali × AC Milan) scored incorrect.
Root cause: both `WikidataClient` intersection builders matched clubs via
the truthy `wdt:P54` shortcut, and **Wikidata's truthy `wdt:` graph
contains only best-rank statements** — the moment an editor marks a
player's *current* club preferred rank (routine Wikidata practice), every
normal-rank historical club vanishes from `wdt:P54` for that player.
"Ever played for" silently became "currently plays for" for exactly the
players whose items are well-maintained enough to use ranks. Fixed by
switching both builders to the full statement path (`p:P54`/`ps:P54`)
with only `wikibase:DeprecatedRank` excluded via `MINUS`; the club-club
builder needs **two distinct statement variables** (one statement can't
point at two clubs). `P106`/`P27`/`P21`/`P569` deliberately stay truthy —
best-rank is the right semantics for those. Don't "simplify" P54 back;
the trap is invisible in testing against players without preferred-rank
statements. Pinned as REQ-113; the query-shape comment in
`WikidataClient.cs` carries the full reasoning.

**Why this was worse than S-037's wrong-QID incident:** that one tainted
4 named clubs; this one made the cached data of **every seeded club**
suspect-incomplete at once — every club row ever fetched under the truthy
query may be missing that club's since-transferred former players. And
**re-warming alone cannot repair it**: `PlayerCacheWarmingService` skips
any pair already at `>= MinValidAnswers` cached answers, so a partial
(but non-empty) cached pair is never re-queried — the stale rows have to
be swept first. Hence the new `clean-stale-club-attributes --all-clubs`
mode (`StaleClubAttributeCleaner.CleanAllSeededClubsAsync`): resolves
every club name from `ClubDefinition` at runtime instead of hand-typing
~32 names (one typo = one club silently left stale, since the named mode
can't tell a typo from nothing-to-clean). Fails loudly on an empty
`ClubDefinition` table (wrong DB / never seeded), and the named form now
rejects any `-`-prefixed token so a mistyped `--all-club` can't
masquerade as a club name that "removed 0 rows" successfully.

**Operator recovery order, strictly:** (1) deploy the query fix, (2)
`clean-stale-club-attributes.yml` once with input `--all-clubs`, (3)
`warm-grid-cache.yml`. Never clean after a fresh warm — same
can't-tell-old-from-new reasoning as S-037/S-038, nothing in the
persisted rows records which query shape fetched them.

### 2026-07-17 — `RoundDuration`/cron coupling replaced (REQ-301, ADR-0027)
Supersedes the 2026-07-10 entry above's hand-matched Tue+Fri cron pairing:
`generate-round.yml` now runs a daily cron left deliberately uncoupled from
the now-config-bound `RoundDuration`, relying on `RoundGenerationService`'s
existing idempotency skip instead of an exact-gap match. Full reasoning,
the rejected `*/2` day-of-month alternative, and the resulting
`RoundDuration >= 24h` invariant: see ADR-0027 (not re-derived here).

**Open item, needs manual live-Wikidata verification** (sandbox can't
reach `wikidata.org`): the same investigation surfaced a Tonali
"Tottenham" attribution in cached data. Could be a genuine post-2026
transfer (training-data knowledge here is stale by definition) or an
S-037-class wrong QID resolving to the wrong entity — the user has a
checklist for verifying it against live Wikidata pages. Don't assume
either way until checked.

### 2026-07-17 — Npgsql provider patch lagging EF Core patch produces a benign MSB3277 warning
After merging a batch of Dependabot PRs, `Microsoft.EntityFrameworkCore`/
`.Design` moved to 10.0.10 (direct PackageReference in
`XGArcade.Api`/`XGArcade.Data`) while `Npgsql.EntityFrameworkCore.PostgreSQL`
moved to 10.0.3 — the newest version published at the time — which still
declares a floor dependency on `EntityFrameworkCore.Relational >= 10.0.4`.
The two package families haven't released in lockstep, so every backend
build (and any workflow that does `dotnet build`/`dotnet run` against
`XGArcade.Api`) prints an `MSB3277` "found conflicts between different
versions of Microsoft.EntityFrameworkCore.Relational" warning for
`XGArcade.Core`, `XGArcade.DataSync`, and `XGArcade.Games.XGGrid`. This is
exactly the risk `implementation-document.md` §1 already calls out for the
Npgsql provider ("it typically follows .NET's release within weeks, but
confirm before committing"). It's a build-time warning only — every CI run
and a manual `import-player-name-index` run completed (exit 0) with it
present. Don't be alarmed by it; revisit once Npgsql publishes a
10.0.10-tracking patch (should make the warning disappear on its own), and
only chase it earlier if something actually breaks. **Unrelated** to the
`import-player-name-index` timeout entries below — that job's 0-rows outcome
was a separate pre-existing issue that just happened to surface in the
same log, not caused by this warning.

### 2026-07-17 — `import-player-name-index` always upserted 0 rows: 15s client timeout too tight for the unfiltered page query
Three consecutive manual runs of `import-player-name-index.yml` all
completed successfully but upserted 0 `PlayerNameIndex` rows, each logging
`Wikidata player-pool page query timed out at offset 0; treating as empty
page` at almost exactly 15s into the query — `WikidataClient`'s
`_queryTimeout` default. That default was tuned in ADR-0011 for the
narrow per-cell country/club *intersection* queries (9-27s observed under
load); `PlayerNameIndexImporter`'s page query has no club/country filter
at all (S-032/ADR-0007's broad player-pool scan), so it's a heavier WDQS
query than ADR-0011's evidence covers, and 15s was consistently too tight
for it — not occasional flakiness. Fixed by passing a 60s `queryTimeout`
specifically to the standalone `WikidataClient` the `import-player-name-index`
CLI verb constructs in `Program.cs` (separate from the DI-registered
client ADR-0011 governs, so the interactive guess-time/grid-generation
paths keep their 15s default unchanged). If 60s still isn't enough, the
next step is checking whether WDQS's own server-side timeout (~60s) is the
real ceiling, not just raising the client-side number further.

**Superseded by the 2026-07-18 entry below — the 60s bump did not and
could not fix it.**

### 2026-07-18 — `import-player-name-index` 0-rows root cause: WDQS's ~60s SERVER-side cap; `ORDER BY` over the whole pool was the real cost — never "fix" this by raising a client timeout
The 60s client timeout above changed nothing: every player-pool page query
still timed out, because the binding limit was never the client's. WDQS
enforces a hard ~60-second **server-side** query timeout that no
client-side setting can raise, and the paged query's inner
`SELECT DISTINCT ?player ... ORDER BY ?player LIMIT 5000 OFFSET n` forced
WDQS to materialize and sort the ENTIRE unfiltered male-footballer pool
(hundreds of thousands of items) on every single page request — so every
page blew the server cap, `QueryPlayerPoolPageAsync`'s swallow-to-`[]`
contract turned the timeout into a phantom empty page, the importer read
it as end-of-data, and the job exited 0 having imported nothing. (The
S-032 quality review had flagged exactly this empty-page/failure ambiguity;
it was the 100% case, not an edge case.) Fixed 2026-07-18 by replacing
OFFSET pagination with birth-year slicing — one bounded one-year `P569`
window per query (1939 → current year), no `ORDER BY`/`LIMIT`/`OFFSET`,
same size class as the intersection queries that work fine — plus a
fail-loud contract: the slice method throws `WikidataQueryException` on
failure (empty year ≠ failure), the importer retries a slice up to 3 times
and fails the whole run (red workflow) if any slice still fails.
Two lessons worth keeping: (1) if a WDQS query times out at ~60s, the
query shape is the problem — bumping any client timeout past 60s is
self-deception because the server cap binds first; (2) a "never throws,
returns empty" client contract is wrong for a bulk job whose success
metric IS the row count — that contract belongs only to the interactive
intersection queries, where REQ-103 genuinely wants failure to look like
no-match. `PhotoUrl`/P18 was dropped in the same fix (nothing ever read
it; `RemovePlayerNameIndexPhotoUrl` migration).

### 2026-07-18 — `backfill-player-photos` crashed on a malformed `WikidataQid`: `ArgumentException` isn't a `WikidataQueryException`, so the batch loop's `catch` never saw it
A real `dotnet run -- backfill-player-photos` run against a live Postgres
database (seeded with this repo's own `/internal/test-data` E2E fixtures,
whose `Player.WikidataQid` values look like `Qtest-<guid>`) crashed with an
unhandled `System.ArgumentException: Not a valid Wikidata QID: '...'`
instead of completing. `WikidataClient.QueryPlayerPhotosByQidsAsync`
validates every QID in the batch up front and throws a plain
`ArgumentException` on the first bad one (same pattern the two
intersection-query methods use, where that's correct — their QIDs come
from hand-curated `CategoryValueRepository` data, so a bad one really is a
caller bug worth crashing loudly for in development).
`PlayerPhotoBackfillService.BackfillAsync`'s per-batch loop only catches
`WikidataQueryException`, so the `ArgumentException` propagated straight
through `Program.cs` and killed the whole run — the opposite of the
service's own documented log-and-continue design (a batch that fails to
fetch photos is supposed to just leave those players' `PhotoUrl` NULL for
the next run to retry). Fixed by extracting the QID-format check into a
shared `WikidataQid.IsValid` helper and having `PlayerPhotoBackfillService`
pre-filter each batch with it *before* calling
`QueryPlayerPhotosByQidsAsync`, logging one warning per skipped player,
rather than wrapping the exception at the client (which would have
sacrificed the other up-to-199 valid QIDs in the same batch to one bad
row). `WikidataClient`'s own `ArgumentException` contract on all three
validating methods is unchanged — this fix is entirely about never letting
an arbitrary DB row's data quality reach that validation in the first
place. Regression tests:
`PlayerPhotoBackfillServiceTests.REQ214_BackfillAsync_BatchContainsMalformedWikidataQid_SkipsThatPlayerButBackfillsTheRestWithoutThrowing`
and the all-malformed edge case,
`REQ214_BackfillAsync_EveryPlayerInBatchHasMalformedWikidataQid_CompletesWithoutThrowing`.

### 2026-07-25 — Supabase's "Enable Captcha Protection" toggle is project-wide, not per-endpoint — it broke real-user login/signup, not just `/auth/guest`
Reported live: a registered user's login started failing with `captcha
protection: request disallowed (no captcha_token found)`, and new signups
started returning the generic "Check your email to confirm your account,
or reset your password if you already have one." message (that message is
`AuthController.Signup`'s deliberate, REQ-701 account-enumeration-safe
fallback for *any* Supabase signup rejection, not a real email-confirmation
feature — it fires for this too). Root cause: ADR-0037 designed Turnstile
captcha as scoped to `POST /auth/guest` only, and `AuthController.Login`/
`Signup` were built assuming Supabase's captcha requirement would only
apply to the anonymous-sign-in call `SignInAnonymouslyAsync` makes
(`SignInWithPasswordAsync`/`SignUpAsync` in `SupabaseAuthClient.cs` never
send a `captcha_token` at all). That assumption was wrong: Supabase's
dashboard "Enable Captcha Protection" setting is a single project-wide
toggle covering every `gotrue` endpoint that can create or authenticate an
identity (`signup`, `token?grant_type=password`, `recover`, ...), not
scoped to whichever flow you had in mind when you turned it on. The
moment someone completed `SETUP.md` step 6 (turning the toggle on for
guest-creation bot protection) against a real Supabase project, it started
rejecting every password-based login and signup too, since those code
paths have no token to send. Same root cause explains both user-visible
symptoms — the misleading signup message wasn't a new feature, it's the
existing anti-enumeration fallback firing for a captcha rejection instead
of a real duplicate-email case. This was flagged as an "unverified against
a live Supabase project" assumption in the original ADR-0037/CHANGELOG
entries (no network access in that sandbox) — now confirmed live and
wrong. Fix needs Turnstile wired into Login and Signup too (not just
Guest), or the captcha toggle turned back off until that's done — tracked
as a separate session, not fixed in this note-taking pass.

### 2026-08-01 — `warm-grid-cache.yml` stopped completing entirely; the 2026-07-28 same-run-retry fix was itself the regression (REQ-110, ADR-0052)

Reported: run #15 was manually re-dispatched three times (2026-07-28
through 2026-08-01) and every attempt got cancelled at the workflow's
90-minute ceiling without ever finishing, on top of CI logs that had
become unreadable — thousands of per-pair `Warning`-level lines, some
carrying 15-20 line stack traces.

Root cause traced to the 2026-07-28 "cache-warming-specific timeout and
same-run retry" extension itself: the same-run retry doubled every
technical failure's cost (up to 2 x the 45s cache-warming timeout instead
of 1x), and nothing persisted a technical failure across runs, so the same
doomed pairs got retried, at that now-doubled cost, on literally every
future run forever. Reading run #15's tail log showed a long contiguous
stretch where *every* club-club pair involving a handful of specific clubs
failed — not intermittently, every single time, regardless of partner
club. One failure named the actual mechanism: a JSON parse error at
binding row 250,204.
`WikidataClient.BuildClubClubIntersectionQuery`'s plain join on two
independent P54 statement-path patterns binds both statement variables in
the outer pattern — a player with multiple non-deprecated P54 statements
at club A (loan spells, a return transfer) times multiple at club B
produces one result row per *combination*, on top of the query's existing
per-alias multiplication. For two clubs with a large, well-documented,
historically-overlapping squad this produced a real 250,000+ row WDQS
response. No timeout, however long, reliably finishes that — it needed a
smaller query, not a bigger budget or more retries.

Fixed by ADR-0052, three changes together: (1) removed the same-run retry
— it only ever helps a transient failure, and made a structural one's cost
worse; (2) added `PairLookupFailure` (mirrors `ConfirmedLowMatchPair`'s
shape, ADR-0050) so a pair failing on 2 *consecutive runs* is skipped
without a live query on the third, converging instead of re-fighting the
same doomed pairs forever — a single run's failure alone is NOT enough to
skip, so a one-off transient blip still gets a real second chance; (3)
wrapped each club's P54 match in `BuildClubClubIntersectionQuery` in its
own `FILTER EXISTS { }` block instead of a plain join, eliminating the
statement-count cross product at the source. Also downgraded the two
per-pair failure logs in `WikidataClient` from `Warning` to `Debug` (the
project's default log level is `Information`, so these are now silent by
default) — the run's own `Information`-level summary already reports the
technical-failure count and names every failing pair, so the per-pair
noise added nothing an operator needed by default.

**Same "don't repeat this reasoning" lesson as the 2026-07-13 wrong-QID
entry above, different shape**: a fix aimed at one real problem
(swallowed-failure visibility) quietly made a different cost worse
(doubled the price of every failure) in a way that only showed up once the
*rate* of failures crossed some threshold — three quiet, successful runs
(2026-07-26/27, pre-extension) gave no signal that the extension itself
would tip the job over its CI budget the moment failures got common
enough. **If cache-warming ever stops completing again**: check the run's
tail log for a long *contiguous* stretch of failures against the *same*
handful of QIDs on one side — that pattern means structural (a query-shape
problem), not transient (WDQS load) or a straightforward "budget's too
small" — a bigger timeout or more retries will not fix a structural
failure, only a query-shape change or `PairLookupFailure`'s skip will.

### 2026-08-02 — `import-player-name-index` crashed on birth year 1970 with an EF identity-tracking conflict; fixed by deduping a batch by PlayerId

A real manual dispatch (run #6, attempt 1) imported birth years 1939-1969
cleanly (57,157 rows) then died with an unhandled
`System.InvalidOperationException: The instance of entity type
'PlayerNameIndexWord' cannot be tracked because another instance with the
same key value for {'PlayerId', 'Word'} is already being tracked`, thrown
from `PlayerNameIndexRepository.ReconcileWords`'s `PlayerNameIndexWords.Add(...)`
call, at exit code 134. Confirmed via the actual GitHub Actions job log
(job 91462586266), not guessed.

Root cause: `UpsertManyAsync` assumed every `PlayerId` in the batch it's
given is unique, and built `existingWordsByPlayer` once, up front, from the
database. If the same `PlayerId` appears twice in one batch, the second
occurrence's `ReconcileWords` call tries to re-`Add` `PlayerNameIndexWord`
rows the first occurrence already staged (Added but not yet saved) for the
same `PlayerId` — EF's change tracker rejects the second `Add` for an
identical `{PlayerId, Word}` composite key immediately, in memory, before
`SaveChangesAsync` ever issues a query. This is distinct from — and not
covered by — the two cases already handled and tested: a repeated word
*within* one name (`ToHashSet()`-deduped, `REQ208_UpsertManyAsync_NameWithRepeatedWord...`)
and the same QID appearing in *two different* birth-year slices, i.e. two
separate `UpsertManyAsync` calls (`ImportAsync_SameQidInTwoBirthYearSlices...`,
which works correctly because each call re-reads current DB state fresh).

**The exact Wikidata-side trigger for birth year 1970 specifically producing
two same-QID entries within one `QueryPlayerPoolBirthYearAsync` response is
NOT confirmed** — `ParseNameIndexBindings` already dedupes by QID string
within one response via its `byQid` dictionary, so this would require
either a WDQS response anomaly on that specific query or something else not
reproducible from this sandbox (no live Wikidata access, same limitation as
every other Wikidata data question in this file). Rather than chase that
further, fixed defensively where it actually matters: `UpsertManyAsync` now
collapses `entryList` by `PlayerId` (`GroupBy(...).Select(g => g.Last())`)
before doing anything else, so a duplicate within one batch can never reach
`ReconcileWords` at all, regardless of cause — "last entry wins," the same
last-write-wins rule this method already applies across separate runs.
Regression test:
`PlayerNameIndexRepositoryTests.UpsertManyAsync_SamePlayerIdTwiceInOneBatch_DoesNotThrow_LastEntryWins`.
**If this run was re-dispatched before this fix lands, it will fail again at
the same point** — re-run `import-player-name-index.yml` once this fix is
on `main`.

### 2026-08-02 — xG Path's "no Celtic at all, missing Juventus/Marseille stints" report explained: `PlayerCareerStint` is a side effect of xG Grid's country×club lookups, not a full career fetch

Live report: a Timothy Weah xG Path puzzle showed no Juventus or Marseille
stints (both real, per Wikipedia) and no Celtic stint at all. Traced
through the actual data path, not guessed:

- `PlayerCareerStint` rows are populated **only** as a side effect of
  `WikidataLookupService.LookupAndPersistAsync` — the nationality × club
  intersection query xG Grid uses to fill a grid cell (ADR-0042/S-079's own
  comment says this explicitly: career-stint persistence is wired up for
  the country/nationality × club path only, deliberately not for
  club-club/trophy-country/trophy-club). There is no "fetch this player's
  full Wikidata career" call anywhere in this codebase — a player's
  `PlayerCareerStint` set is whatever the accumulated history of xG Grid
  cell lookups (live guess-time misses + `warm-grid-cache` runs) has
  happened to query so far, never a complete career.
- A stint can only ever be recorded for a club that is in the seeded
  `ClubDefinition` table (`ReferenceDataSeeder.Clubs`) — the intersection
  query is always (seeded country, seeded club). **Celtic is not in that
  list at all** (checked `ReferenceDataSeeder.cs` directly), so a Celtic
  stint can never be persisted for any player, regardless of how much
  cache-warming runs — this is a reference-data gap, not a bug to fix in
  code. Juventus and Marseille, by contrast, **are** both seeded — so a
  missing stint there means the specific (nationality, club) pair (for
  Weah, presumably United States of America × Juventus and
  USA × Marseille) simply hasn't been queried and cached yet, or was
  queried and marked a confirmed-low-match/technical-failure pair
  (ADR-0050/ADR-0052) and is now being skipped without a live re-query.
- `PathEndpoints`'s `GET /path/current` renders every `PlayerCareerStint`
  row on record for the target player, unfiltered by seeded-club status —
  so this isn't a display bug hiding data that exists; the rows genuinely
  aren't there yet.

**Not a code defect** — this is `ADR-0042`'s accepted scope (career stints
are a byproduct of xG Grid's own cell lookups, not a first-class Wikidata
fetch) intersecting with two separate, known gaps: Celtic's absence from
`ClubDefinition`, and `warm-grid-cache` not yet having covered every
(nationality, seeded club) pair for every xG Path target player. Two
distinct fixes exist if this is worth acting on, not attempted here since
neither was asked for: (1) add Celtic to `ReferenceDataSeeder.Clubs` with a
verified QID (same S-037-style live-Wikidata verification discipline every
other club addition here has followed — this sandbox can't verify it), and
(2) either run `warm-grid-cache.yml` again to close remaining
(nationality, club) gaps, or give xG Path its own direct per-player
career-stint fetch instead of depending on xG Grid's lookup history as a
byproduct — the latter is a real scope decision (a new Wikidata query
shape, ADR-worthy), not a one-line fix.

## PlayerNameIndex.BirthYear can go ambiguous — Wikidata itself, not our bug (2026-08-03)

A user-tester report: the autocomplete suggestion for "Michael Owen" (the
England footballer, actually born 14 December 1979) showed birth year 1976.
`wdt:P569` is a *truthy* predicate — it already collapses to a single
preferred-rank statement whenever Wikidata has one, so this can only happen
when the underlying Wikidata item genuinely carries more than one
non-deprecated P569 (date of birth) statement with **neither** marked
preferred — Wikidata's own data has no stated preference between them.
This is a real, if uncommon, state of Wikidata's data (an old or
erroneous secondary-sourced date nobody has cleaned up), not something our
SPARQL can resolve with certainty.

Before this fix, two independent code paths silently picked one of the
conflicting values with no correctness signal behind the choice:
`WikidataClient.ParseNameIndexBindings` kept whichever row happened to
arrive first in a single SPARQL response's (unspecified, engine-internal)
row order, and `PlayerNameIndexImporter.ImportAsync`'s per-birth-year-slice
loop let whichever slice ran *last* (i.e. whichever value is numerically
higher, since the loop runs ascending 1939 → current year) silently
overwrite the other. For Michael Owen specifically the second mechanism
would have landed on the *correct* value by coincidence (1979 > 1976) — the
report only surfaced because whatever earlier data was actually
persisted for his `PlayerNameIndex` row predates this reasoning, or the
import run that would have corrected it to 1979 never completed. Either
way, "later wins" was never a principled rule, just a happy accident when
it worked.

Fixed by treating a genuine cross-row/cross-slice birth-year conflict as
unresolvable and nulling out `BirthYear` instead of guessing either way —
same "omit rather than mislead" convention this codebase already uses for
an unknown club appearance count. See `ParseNameIndexBindings`'s and
`PlayerNameIndexImporter.ResolveCrossSliceBirthYearConflicts`'s own doc
comments for the mechanics.

**Sandbox limitation, flagged rather than silently worked around:** this
session had no outbound network access to `wikidata.org` (the agent
proxy's egress policy doesn't allow it) or to `dotnet.microsoft.com` (the
.NET SDK isn't preinstalled here and couldn't be downloaded either), so
neither the live Wikidata data behind the original report nor a real
`dotnet build`/`dotnet test` run of these changes could be verified
directly in this session — the fix and its tests were checked by careful
manual review (hand-verified brace/paren balance, cross-checked against
this file's other `record`/tuple `with`/mutation patterns that already
compile elsewhere in this codebase) instead. Worth an actual CI run before
merging.

## ADR-0061's team-competition trophy work also needed a 4th, unlisted query method (2026-08-09)

While implementing ADR-0061 (FIFA World Cup/UEFA Champions League as
team-competition trophies), resolving ADR-0035's follow-up note — "extend
`BuildTrophyCountryIntersectionQuery` with a `P1532` counterpart... whenever
the trophy pool grows enough to make the pairing reachable" — turned out to
require more than the three `IWikidataClient` methods ADR-0061 itself lists
(`QueryTeamTrophyCountryIntersectionAsync`/
`QueryTeamTrophyNationalTeamIntersectionAsync`/`QueryTeamTrophyClubIntersectionAsync`).
ADR-0035's note was about `LookupAndPersistTrophyCountryAsync` honoring
`CountryDefinition.UsesCountryForSportProperty` **in general**, not only for
the new team-trophy branch — so the *existing*, pre-ADR-0061 individual-award
P166 path (S-031) needed its own P1532 counterpart too, or a flagged country
(England, Scotland, Wales, Northern Ireland) paired with an individual award
like Ballon d'Or would still silently fall back to (wrong) P27 semantics,
just one branch over from the one ADR-0061 explicitly fixed.

Added a fourth method, `QueryTrophyNationalTeamIntersectionAsync` (P166
truthy + P1532 truthy, the individual-award counterpart of
`QueryTeamTrophyNationalTeamIntersectionAsync`'s team-competition shape), and
a matching `BuildTrophyNationalTeamIntersectionQuery` builder — not written
into ADR-0061's own text (that ADR's "Decision" section literally says
"three new methods"), but documented at length on the method/builder
themselves and in ADR-0035's now-resolved follow-up note, since it's the
same P27-vs-P1532 dispatch pattern ADR-0035 already established, applied to
close a gap that ADR's own note had explicitly flagged. Flagging here in
case a future reviewer greps ADR-0061 for "how many new client methods"
and is confused why the actual diff has four, not three — the answer is
"ADR-0035's follow-up note, read literally, covers a case ADR-0061's own
scope didn't."

**Sandbox limitation:** as with every other session in this codebase, no
outbound network access to `wikidata.org` and no `dotnet` SDK available —
the two new team-trophy QIDs (`Q19317` FIFA World Cup, `Q18756` UEFA
Champions League) are training-knowledge guesses, not verified, and none of
the new/changed code in this story was run through a real `dotnet build`/
`dotnet test` — checked by careful manual review (brace/paren balance,
cross-referencing existing query-builder/dispatch patterns) instead. Must
run in CI before merging, and the two QIDs must be checked against live
Wikidata pages by a human before this is relied on in production.

### 2026-08-09 — Admin Wikidata by-name lookup (`REQ-509`/`REQ-510`) failing with "Lookup unavailable" in production: same 15s-too-tight-for-a-broad-query-shape bug as `import-player-name-index`/`PlayerCareerPrefetchService`, just newer code

Reported live (via a screenshot of the admin Suggestions screen): both
`POST /admin/suggestions/{id}/lookup` (REQ-509, suggestion review) and
`POST /admin/player-search/lookup` (REQ-510, standalone admin search)
were failing with "Lookup unavailable — we couldn't reach Wikidata to
verify this player" — for the *same* player, on the *same* attempt, on
*both* endpoints, which was the tell: they share one code path
(`AdminSuggestionEndpoints.LookupPlayerAsync` →
`IWikidataClient.QueryPlayerCareerAndNationalityByNameAsync`), so a
Wikidata reachability/timing problem takes out both admin recovery
routes for a stuck suggestion at once, with no fallback between them.

Root cause: `QueryPlayerCareerAndNationalityByNameAsync`'s query
(`BuildPlayerCareerAndNationalityByNameQuery`) does a case-insensitive
`rdfs:label`/`skos:altLabel` scan across every Wikidata footballer
(`?player wdt:P106 wd:Q937857` + a `LCASE(STR(?matchedLabel)) =
LCASE(...)` filter) to find a name match — an unindexed, population-wide
scan, not a narrow per-cell query — but it was still using
`WikidataClient`'s default `_queryTimeout` (15s), the budget ADR-0011
tuned specifically for the narrow per-cell intersection queries. This is
the exact same failure class recorded twice already in this file
(2026-07-17 `import-player-name-index`, 2026-07-28
`PlayerCareerPrefetchService`) — a broad/unbounded WDQS query shape
needs its own, longer, dedicated timeout, and 15s is only ever safe for
the narrow per-cell shape it was measured against. This admin lookup
(built in S-090, 2026-08-08) just hadn't been through that lesson yet,
being newer than both prior incidents.

Fixed the same way both prior incidents were: added a fourth,
purpose-specific `TimeSpan?` constructor param on `WikidataClient`,
`adminLookupQueryTimeout` (`_adminLookupQueryTimeout`, defaulting to
45s — same evidence band as `_cacheWarmingQueryTimeout`'s 45s, safely
under WDQS's ~60s hard server-side cap per the 2026-07-18 entry above,
but explicitly *not* framed as "nobody's waiting" the way cache warming
is — an admin is synchronously blocked on this in a browser tab), used
only by `QueryPlayerCareerAndNationalityByNameAsync`. Deliberately did
**not** touch `QueryPlayerPhotoByNameAsync` (same by-name shape, but on
the live wrong-guess photo-reveal path, ADR-0057, with a real-time/
ingress-timeout constraint this admin lookup doesn't have) — its
existing comment claiming budget parity with the career/nationality
lookup was updated to explain the two now intentionally diverge, and a
new regression test
(`REQ216_QueryPlayerPhotoByNameAsync_UsesQueryTimeout_NotAdminLookupBudget`)
locks that down so a future refactor can't silently re-merge the two
budgets and only find out via a real 45s slowdown in production.

Also added server-side `Warning`-level logging (with the caught
`WikidataQueryException`) in both `/admin/suggestions/{id}/lookup` and
`/admin/player-search/lookup`'s existing catch blocks — before this fix,
neither endpoint logged anything on failure, so a recurrence would have
been just as undiagnosable as this one was. The HTTP 503 response
contract is unchanged (ADR-0046 — the exception's own message still
never reaches the admin's browser, only the server log).

Went through `architecture-reviewer` (no ADR needed — same "just tuning"
judgment as the two prior timeout additions, `_guessTimeFallbackQueryTimeout`/
ADR-0046 and `_cacheWarmingQueryTimeout`/REQ-110, neither of which got
its own ADR either; flagged as a forward-looking note that a *fifth*
per-purpose timeout on this constructor would be the point to reconsider
the growing-parameter-list shape) and `quality-architect` (no blocking
findings; one real gap closed — see the regression test above — plus two
non-blocking follow-ups noted for later: `AdminSuggestionEndpointTests.cs`'s
`CapturingLoggerProvider` is now duplicated with `GridEndpointTests.cs`'s
copy in the same test assembly, worth extracting next time either file
is touched; and the constructor's four same-typed optional `TimeSpan?`
params are becoming error-prone by position, worth an options-object
refactor if a fifth is ever proposed).

**Whether this actually fixes it in production is still unverified** —
this session had no live Wikidata access to reproduce the original
failure or confirm 45s clears it (same standing sandbox limitation as
every entry in this file); the user was explicit up front that they were
skeptical a timeout bump alone would hold, given this class of lookup's
recurring history. The new Warning-level logging exists specifically so
that if "Lookup unavailable" recurs after this ships, there's now a
server log entry (with the suggestion id or player name, and whether it
was a timeout vs. an HTTP/parse failure) instead of nothing to go on —
check the logs first before assuming the timeout value itself needs
raising further, since a genuine WDQS outage or a non-timeout failure
(HTTP error, malformed JSON) would look identical to a timeout from the
admin's browser but very different in this new log line.

**Update, same day, ~90 minutes later — the new logging paid off
immediately, and confirmed the user's skepticism was right.** A real
production log (pasted into this session, not reproduced here) for the
exact same player (Donny van de Beek) showed the request running **38.8
seconds and then returning HTTP 502 Bad Gateway** from
`query.wikidata.org` — not a timeout at all. The 45s budget above was
never the bottleneck; it gave the request enough room to actually
complete and reveal what's really happening: something in front of WDQS
(most likely a gateway/reverse-proxy enforcing its own upstream-response
deadline, independent of any client-side `CancellationTokenSource`) is
rejecting this query once it runs long enough. No client timeout, however
large, fixes a failure that happens on the far side of that gateway.

Root cause, once actually visible: `BuildPlayerCareerAndNationalityByNameQuery`'s
candidate-selection subquery was a case-insensitive `rdfs:label`/
`skos:altLabel` scan across every Wikidata footballer — an unindexed,
population-wide literal comparison, not a narrow query. That's what was
actually expensive; the timeout fix above only ever addressed the
symptom (client giving up too early), never the cause (WDQS itself, or
its gateway, choking on the query's real cost).

Fixed in ADR-0062 (`docs/decisions/0062-admin-lookup-wikibase-mwapi-search.md`):
replaced that scan with a federated `SERVICE wikibase:mwapi { ... }`
`EntitySearch` call — Wikidata's own indexed search, the same engine
behind its search box — re-filtered to footballers, still `LIMIT 1`. Two
alternatives were considered and rejected: backfilling a real
`WikidataQid` column onto `PlayerNameIndex` so this could resolve locally
(rejected — `PlayerNameIndex.PlayerId` is a one-way hash of the QID
today, not the QID itself, and reconciling the two id spaces is exactly
the kind of deliberate future decision ADR-0007/COMP-10's own comments
say not to back into via a fix); and calling Wikidata's REST
`wbsearchentities` API directly (rejected — a genuinely new external
host/dependency needing its own ADR-0008-style terms review, plus two
round trips instead of one). The chosen approach stays inside
`WikidataClient`'s existing single-endpoint SPARQL client — the `SERVICE
wikibase:mwapi` federation is executed server-side by WDQS itself against
`www.wikidata.org`, not by this codebase's own `HttpClient` — confirmed
by `architecture-reviewer` on direct scrutiny, not just deferred to the
ADR's own claim.

**Still unverified against the real endpoint** — same standing sandbox
limitation (no live network access to `wikidata.org`). Specific things a
human should check first, per the implementer's own flagged uncertainty:
whether `mwapi:limit "10"` is actually respected by the live WDQS
deployment; whether re-filtering `EntitySearch`'s ranked candidates down
to `wdt:P106 wd:Q937857` can legitimately return zero results for an
ambiguous name where a much more famous non-footballer outranks the
actual player in the top 10 (try "Donny van de Beek" first as a known
real case, then a deliberately ambiguous name); and whether
`wikibase:endpoint "www.wikidata.org"` is the exact right form for this
WDQS deployment. `QueryPlayerPhotoByNameAsync` (REQ-216/ADR-0057) still
uses the old raw label-scan shape — deliberately not touched here (its
own different timeout/urgency constraints), flagged in ADR-0062 as a
possible future follow-up if this pattern proves out.

**The general lesson, worth internalizing beyond this one incident:** a
client-side timeout increase can only ever fix "our client gave up too
early." It does nothing for "the query is too expensive and something
else — the server, or infrastructure in front of it — rejects it once it
runs long enough." Adding diagnosability (the Warning-level logging from
the fix above) before assuming a timeout bump is sufficient is what
turned this from "still broken, no idea why" into a concrete, actionable
root cause within one production log line.

**Update, same day — manually verified against the real
`query.wikidata.org` endpoint by the user (this sandbox still has no live
network access, so this is human-run verification, not automated).**
Running the actual query text for "Donny van de Beek" — the real case
that started this whole thread — returned all 6 real clubs correctly, in
8-31s across repeated runs (never a 502, always well under the 45s
budget). The isolated `wikibase:mwapi` search step alone (no P27/P54
join) accounted for most of that time (6 of 8s on one run) — the search
federation itself is the slow part, not the club-history join this
NOTES.md entry originally suspected; a possible future optimization
target if 8-31s per admin lookup ever becomes a real UX complaint, but
not urgent (it's synchronous but rare, admin-only, and light-years better
than a guaranteed 502).

The "no footballer matches this name" path was also tested (a nonsense
search string): one run returned `Zzxxqq Nonexistentplayer123`-style
searches cleanly with zero rows in ~14s; a separate run of the FULL query
(search + P27 + P54 block) briefly returned an opaque "unknown failure"
on the same nonsense name, but retrying the identical query succeeded
("No matching records found") — consistent with ordinary WDQS
load-related flakiness (the same 9-27s-observed-under-load variance
ADR-0011 already documents), not a reproducible structural bug in the
mwapi rewrite. Re-running the isolated search-only query with the same
nonsense name also succeeded cleanly. Given it didn't reproduce on retry
and the isolated pieces are each individually clean, this reads as
ordinary transient WDQS flakiness (exactly the class of failure ADR-0046's
"lookup unavailable, try again" contract already exists to handle
gracefully), not evidence the query shape itself is broken for the
no-match case — but if "unknown failure" (or any error) on a genuine
no-match search becomes a *repeatable* pattern in production logs (check
the new Warning-level log line from the timeout-fix incident above to
tell timeout/HTTP/parse-failure apart), revisit this assumption; the
open risk ADR-0062 originally flagged (an ambiguous name where a more
famous non-footballer could outrank the real player in the top-10
`EntitySearch` results) was not directly tested and remains open.

This is real evidence the fix addresses the actual production bug (no
more 502 for the one real case that triggered this investigation), gathered
by a human against the live endpoint rather than assumed from documentation
memory — merged on the strength of this, not on CI alone (see PR #157).

## purge-player-pool timed out against a grown pool — needed a longer command timeout, not a code bug (2026-08-17)

First real `purge-player-pool` run since S-038/ADR-0025 originally shipped it
failed with `Npgsql.NpgsqlException: Exception while reading from stream` /
`System.TimeoutException: Timeout during reading attempt`, ~53s into the
"Purge player pool" step, exit code 134. Root cause: `BuildDbContext()`
never sets a `CommandTimeout`, so every CLI verb runs on Npgsql's 30s
default — fine for the small, incremental writes every other verb does,
but `HandlePurgePlayerPoolAsync`'s single `purgeDbContext.Players
.ExecuteDeleteAsync()` cascades through `PlayerData`/`PlayerOverride`/
`PlayerAttribute`/`PlayerAlias`/`PlayerCareerStint` — the last of which
alone had 600k+ rows as of the last real `prefetch-player-careers` run
(this same run's own predecessor, before ADR-0069's club sweep grew the
pool further) — well past what a 30s bulk cascade delete can reliably
finish in. Same class of incident as ADR-0055's own 2026-08-02 entry
(`WikidataClient`'s default query timeout needed bumping for
`prefetch-player-careers` specifically) — a one-off, verb-scoped timeout
override (`purgeDbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(10))`),
not a change to `BuildDbContext`'s shared default every other (much
smaller-scale) verb uses. Fixed same day; re-run confirmed the fix.

## First real post-purge cold rebuild needed more headroom than 90 minutes (2026-08-17)

Right after the purge fix above, both `warm-grid-cache.yml` and
`prefetch-player-careers.yml` were triggered to rebuild the pool from
scratch — the first real run of either against a fully-purged database
(`ConfirmedLowMatchPair`/`PairLookupFailure` cleared too, so
`warm-grid-cache`'s usual "repeat runs are cheap" skip-shortcut had
nothing to skip) and the first run of `prefetch-player-careers` under
ADR-0069's new combined country+club sweep (roughly double the prior
scope). `warm-grid-cache` got killed by its own 90-minute
`timeout-minutes` cap mid-sweep. `prefetch-player-careers` actually
completed its full pass in ~88 of 90 minutes — 193,382 players / 527,252
stints touched — but exited nonzero per its own designed "keep going, fail
loud at the end" contract: 37 career-fetch batches hit transient WDQS 502s
and 2 countries (Sweden, Canada) failed their pool fetch, scattered across
the run, not a contiguous run against the same club/country — read as
ordinary WDQS load flakiness under the heavier sweep, not a code bug, same
class as this job's earlier documented incident. Both workflows' timeouts
raised to 240 minutes to give a genuine cold run real headroom; re-run
after the fix.

Worth remembering: `PlayerCareerPrefetchService` has no skip-already-
processed shortcut the way `warm-grid-cache` does — every re-run repeats
the FULL country+club pool sweep from scratch (only the DB writes are
cheap no-ops for already-persisted data), so a retry costs roughly the
same wall-clock time as the original run, not a fast delta the way a
`warm-grid-cache` re-run against an already-partially-warmed
`ConfirmedLowMatchPair` table would.

## S-131 closed: post-#203 re-run confirmed the timeout fix, not the flakiness, was the fix (2026-08-17)

Verified against real run history rather than assumed. Run #6
(`workflow_dispatch`, created `2026-08-17T08:09:25Z`, `head_sha` =
`1e7cb99` itself — the exact commit from the entry above) is the
manually-triggered post-#203 run S-131 asked for. It finished in 43
minutes (08:09→08:52), comfortably inside the new 240-minute cap, so the
timeout headroom fix genuinely worked — no timeout-related failure this
time. It still exited nonzero, but on a different, already-understood
cause: 8 countries (United Kingdom, Argentina, Germany, Ivory Coast,
France, Brazil, Czech Republic, United States of America) and 1 club
(Lille) failed their pool fetch, and 26 career-fetch batches failed, all
transient Wikidata `502 Bad Gateway` responses plus one truncated-response
JSON parse error — 132,226 players touched / 20,287 stints added from what
succeeded. Same flakiness class as this job's own two prior documented
incidents (run #5 above; the 2026-07-18/07-2x entries earlier in this
file), not a regression and not a new bug — confirmed by reading the
actual job logs rather than assuming timeout was still the cause, per
S-131's own instruction.

Closing S-131 on this evidence rather than reopening it indefinitely.
Filed S-153 (`docs/backlog.md`) as the real follow-up: give
`prefetch-player-careers` the same persisted failure-tracking
(`PairLookupFailure`, ADR-0052) shortcut `warm-grid-cache` already has,
so a re-run only retries the ~35 units that actually failed instead of
repeating the full sweep every time.

## S-136: `generate-round.yml` split into `generate-grid-round.yml` / `generate-path-round.yml` (2026-08-17)

Split the single shared workflow (one job, one `0 6 * * *` cron, calling a
bash retry function once per `GameKey`) into two fully independent
workflow files, one per `GameKey`, each with its own `on.schedule`
(`0 6 * * *`, unchanged) and its own `workflow_dispatch.round_duration_hours`
input. Safe now for reasons that didn't hold at ADR-0051's time:
`RoundSchedulingOptions` is already fully per-`GameKey`
(`IRoundSchedulingOptionsResolver`) and `/internal/generate-round` already
takes `gameKey` as a first-class parameter with no other game-specific
branching — nothing server-side changed for this story. Each workflow's own
`RoundDuration >= cron max gap` invariant (ADR-0027) was re-verified
independently rather than assumed carried over: both games currently
default to 48h `RoundDuration` against each workflow's own 24h daily max
gap, comfortably safe. Side-effect bug fix: the old shared
`workflow_dispatch` input silently applied to both `GameKey`s at once when
supplied for a manual dispatch of one game's round — each new workflow's
input now only affects its own `GameKey`. Full reasoning: ADR-0072 (extends
ADR-0027/ADR-0051, supersedes neither). Repo-wide reference sweep updated
every non-historical mention of `generate-round.yml` by name; ADR-0022,
ADR-0023, ADR-0027, ADR-0051, and `docs/review-2026-07-07.md` were left
untouched as accurate historical record of what was true when they were
written.

## S-152: `purge-game-history` run for real (2026-08-18)

First and only real run, via `purge-game-history.yml`'s `workflow_dispatch`,
once Epics 10-15 were settled (10-13 merged, 14/15 formally cancelled the
same day — see `docs/backlog.md`'s Epic 14/15 cancellation notes). Actual
row counts wiped, from the verb's own log output:

- `Round`: 29
- `Guess`: 230
- `PlayerSuggestion`: 4
- `GridInstance`: 18 (→ `GridCell`: 162)
- `PathInstance`: 11 (→ `PathPuzzle`: 4)
- `PathCycleTargetUsage`: 4
- `PathTargetCycle`: the singleton row existed and was removed

Same incident-log discipline as `purge-player-pool.yml`'s own entries in
this file. `User`/`League`/`LeagueMembership` and every `Player`/reference
table were untouched, as designed — not re-verified here beyond what
`GameHistoryPurgerTests.cs` already proves at the InMemory-provider level,
since this was a real production-adjacent run, not a place to add new
verification steps. No follow-up workflow needed (unlike
`purge-player-pool.yml`'s own "trigger `warm-grid-cache.yml` next" note) —
this verb never touches the player pool, so `generate-grid-round.yml`/
`generate-path-round.yml`'s existing daily `0 6 * * *` cron picks back up
on its own, generating fresh rounds against a clean `SequenceNumber`
sequence starting at 1.

### `generate-grid-round.yml`/`generate-path-round.yml` silently reported success on a total connection failure (2026-08-29)

Both workflows delegate their retry loop to
`.github/actions/trigger-round-generation/action.yml` (S-176). That loop's
success check was `[ "$http_status" -lt 400 ]`. `curl -w "%{http_code}"`
reports the literal string `"000"` — not a real HTTP status — whenever it
never got a response at all (DNS failure, connection refused, a proxying
layer rejecting the request before it reaches the backend). `000` compares
numerically as `0`, and `0 -lt 400` is true, so a totally failed request
was indistinguishable from a real 2xx/3xx response: the loop returned
success on attempt 1 with no retry, no `::warning`/`::error` annotation,
and the workflow run showed green — while no round was ever generated.
Reproduced locally with `curl` against a nonexistent host (exit code 56,
`http_code` `000`) to confirm before fixing, since this sandbox can't reach
the real dev backend to trigger the failure for real. Fixed by requiring
`>= 200` as well as `< 400`, which excludes `000` (and any other
non-3-digit-real-status value) from the success path while leaving real
2xx/3xx and 4xx/5xx handling unchanged. This is a strict subset of what a
real HTTP response would ever report, so no legitimate success case is
newly rejected. No `dotnet`/CI run needed to verify (pure bash inside a
composite action) — verified by extracting the retry loop's logic into a
standalone script and exercising `000`/`200`/`500` cases directly (see this
commit's message for the transcript). If this recurs, check whether the
runner's egress/proxy is rejecting the request before assuming the backend
itself is unhealthy — a `000` status means the request never arrived.
