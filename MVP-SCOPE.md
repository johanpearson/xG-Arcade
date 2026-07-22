# MVP Scope — What to Actually Build First

This document exists because the design work in `docs/` describes the full
long-term vision, and building all of it before anything is playable would
be a mistake. **This file is the real build order.** Everything already
documented stays valid and referenced — nothing here means "delete the
design," it means "not yet."

> **For AI agents:** when starting implementation, build Tier 0 only.
> Don't implement a Tier 1/2 item because its REQ/ADR already exists and
> looks "ready" — that's exactly the trap this document exists to prevent.
> If a task seems to require Tier 1/2 complexity to make Tier 0 work,
> stop and flag it rather than quietly pulling it forward.

## Why this exists

Across many design sessions, real problems kept getting found (a genuine
correctness bug, a missing backup story, a data-provider ToS risk) and
each one got fixed properly, with an ADR. That's individually good
practice — but the cumulative result is a design that's more complete than
a first playable version needs to be. None of those decisions were wrong;
they were just answered before there was any evidence they mattered yet.
This document reverses that ordering: ship something small and real,
then let actual testing decide what Tier 1 work is worth doing.

## Preconditions to actually start (the complete list, nothing more)

This is every account/setup step Tier 0 needs — full detail in `SETUP.md`,
this is the condensed, self-contained version so you don't have to cross-
reference to know if you're ready:

- [ ] GitHub repo created, this doc set pushed to it
- [ ] One Azure subscription, one resource group (`xg-arcade-dev-rg`) —
  see the note below on why this is named "dev," not "prod"
- [ ] Azure OIDC federated credential set up for GitHub Actions
- [ ] **One** Supabase project (the "dev" one — a second, "prod," is Tier 1)
- [ ] Supabase Auth's "confirm email" requirement turned **off** in
  project settings (Tier 0 has no email confirmation flow at all)
- [ ] ~15 clubs' and ~15-20 countries' Wikidata QIDs looked up by hand
  (visit each club's/country's Wikidata page — takes seconds each) and
  entered alongside their names in `CountryDefinition`/`ClubDefinition`.
  **No API-Football account needed for Tier 0 at all** — Wikidata's
  public endpoint needs no account or key. API-Football only becomes
  relevant in Tier 1 (as a fallback source, per the corrected design
  below — which is also where "expand beyond this list" lives; this list
  is deliberately small for Tier 0, not the final scope):

  **Clubs (15) — verified against live Wikidata pages, 2026-07-08:**

  | Club | QID |
  |---|---|
  | Real Madrid | Q8682 |
  | Barcelona | Q7156 |
  | Manchester United | Q18656 |
  | Manchester City | Q50602 |
  | Liverpool | Q1130849 |
  | Arsenal | Q9617 |
  | Chelsea | Q9616 |
  | Bayern Munich | Q15789 |
  | Borussia Dortmund | Q41420 |
  | Juventus | Q1422 |
  | AC Milan | Q1543 |
  | Inter Milan | Q631 |
  | Paris Saint-Germain | Q483020 |
  | Ajax | Q81888 |
  | Benfica | Q131499 |

  **Countries (20) — verified against live Wikidata pages, 2026-07-08:**

  | Country | QID |
  |---|---|
  | Brazil | Q155 |
  | Argentina | Q414 |
  | France | Q142 |
  | Germany | Q183 |
  | Spain | Q29 |
  | United Kingdom | Q145 — **not England** — see note below for why |
  | Italy | Q38 |
  | Netherlands | Q55 |
  | Portugal | Q45 |
  | Belgium | Q31 |
  | Croatia | Q224 |
  | Uruguay | Q77 |
  | Colombia | Q739 |
  | Nigeria | Q1033 |
  | Senegal | Q1041 |
  | Ivory Coast | Q1008 |
  | Serbia | Q403 |
  | Poland | Q36 |
  | Sweden | Q34 |
  | Denmark | Q35 |

  **Why United Kingdom, not England:** England isn't a sovereign state, so
  Wikidata has no separate "English citizenship" — English players' `P27`
  citizenship is uniformly `Q145` (United Kingdom), same as Scottish and
  Welsh players. Querying `P27 = Q21` (England) directly would return
  nothing. Using United Kingdom instead means Tier 0's country list is
  fully uniform — every row uses the same `P27` query, no exceptions, no
  special-case code path. The tradeoff, made deliberately: "United
  Kingdom" is a less natural football category than "England" specifically
  (fans think in terms of England/Scotland/Wales, not "the UK"). That
  tradeoff is accepted for Tier 0's simplicity and revisited properly at
  Tier 1 — see below.

- [x] QID lookups complete (table above) — entering these into
  `CountryDefinition`/`ClubDefinition` is now just data entry, no more research needed

- [ ] GitHub repo secrets set: `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/
  `AZURE_SUBSCRIPTION_ID`, `INTERNAL_JOB_TOKEN`, `DEV_AZURE_RESOURCE_GROUP`,
  `DEV_DATABASE_CONNECTION_STRING`, `DEV_SUPABASE_URL`,
  `DEV_SUPABASE_ANON_KEY` (the last three filled in
  after step 2's Supabase project exists — the backend calls Supabase
  Auth's REST API directly to mediate signup/login, ADR-0013, rather than
  the frontend calling Supabase itself; `ci.yml`'s local E2E stack doesn't
  need any of these, since it runs with `Auth__Mode=local-e2e` instead, see
  `docs/backlog.md` S-004; JWT validation needs no separate secret at all —
  it derives from `DEV_SUPABASE_URL` alone, ADR-0017); `DEV_SUPABASE_SERVICE_ROLE_KEY`
  (added S-025/ADR-0026 — a genuinely privileged credential, unlike the anon
  key, used only by REQ-710's self-service account deletion to remove the
  Supabase Auth identity; also unneeded by `ci.yml`'s local E2E stack for the
  same `Auth__Mode=local-e2e` reason); `DEV_AZURE_STATIC_WEB_APPS_API_TOKEN`,
  `DEV_BACKEND_HOSTNAME`, and `DEV_FRONTEND_HOSTNAME` filled in after the
  first deploy — the last of these is also what `deploy.yml` feeds to the
  backend as its CORS-allowed origin, so the frontend can't actually reach
  `/health` cross-origin until it's set; see `infra/README.md`)

**Why Tier 0's one environment is named "dev," not "prod":** Tier 0 has no
backups, no email confirmation, no legal docs, no alerting — it's
explicitly built for you testing, not real stakes (see the bright lines in
Tier 1 below). That's what a dev environment is. Calling it "prod" would
have been reusing leftover naming from the original two-environment design,
not a deliberate choice — corrected here. Practical upside: the "dev"
naming already exists (built ahead of when it was needed, during the
environment-split work) — Tier 0 just uses it directly. **Tier 1 doesn't
add a dev environment — it creates the first real "prod"**, at the same
point the backup/alerting/legal-docs bright lines get crossed.

**Explicitly not needed to start**: a second Supabase project, Resend, any
`PROD_*` secret, `RESEND_API_KEY`, an API-Football account. If `SETUP.md`
or `infra/README.md` seem to ask for these before you can begin, that's a
sign those docs have drifted from this one — this file wins.

Once every box above is checked, follow `CLAUDE.md`'s "Getting started"
section to actually scaffold the code.

## Tier 0 — MVP (build this, and only this, first)



**Environment**: one environment, named "dev" (see the precondition note
above for why). No "prod" exists yet — creating one is the Tier 1 trigger
below, not a Tier 0 task. The bidirectional sync scripts/workflows
(ADR-0006/ADR-0009) already exist from earlier design work — nothing new
to build, they just have no second environment to sync with yet, so
they're unused until Tier 1. REQ-801-804's full test-data API (a
persistent, remotely-accessible reset/scenario API) — defer; Tier 0 only
needs REQ-806's much smaller local test-only endpoint (see Epic 3 in
`docs/backlog.md`), scoped to the ephemeral stack `ci.yml` already runs
E2E against.

**Grid content**: Country × Club, plus Club × Club as of `docs/backlog.md`
S-030 (2026-07-12) — REQ-107 already permitted this pairing, Tier 0 grid
generation just never used it; no new reference data needed. REQ-108's
Trophy category is a separate pull-forward, **built 2026-07-20 as S-031** —
see the Tier 1 section below for why it's scoped narrower than REQ-108's
full definition, and why it's mechanically wired up but structurally
dormant (won't actually be selected) until more than one trophy is seeded.
**Revised, per an explicit decision to prioritize full historical
correctness over club-count breadth**: a small, **hand-curated** list of
roughly **15 clubs** and **15-20 countries** in `CountryDefinition`/
`ClubDefinition` — fewer clubs than originally planned, chosen
specifically to keep the one-time setup (below) small and manual. Each
row is entered with its name **and its Wikidata QID together**, looked up
by hand (visiting the club's/country's Wikidata page takes seconds) — no
automated resolution step, no `ApiFootballTeamId` needed for Tier 0 at all.

**Data source**: **Wikidata only** for Tier 0 — no API-Football in Tier 0
at all. This is a reversal of an earlier version of this plan (which had
Tier 0 using API-Football only, deferring Wikidata) — corrected because
"ever played for this club," not "currently plays," is the actual
requirement, and Wikidata is the tool naturally suited to that.

**How Tier 0 actually fetches matching players — and why full history is
free here, not an extra cost:** Wikidata models a player's career as
multiple statements of the property `P54` ("member of sports team") — one
per club they've ever played for, each usually with career-span
qualifiers. A query checking `P54 = Arsenal` checks *all* of a player's
career clubs at once — "ever played for" isn't a special case to handle,
it's just what the property already means. So for a Country × Club cell
(e.g. France × Arsenal), the query is a direct intersection, no per-season
loop, no squad-fetch-and-filter workaround required:

```sparql
SELECT ?player ?playerLabel WHERE {
  ?player wdt:P106 wd:Q937857.   # occupation: association football player
  ?player wdt:P27 wd:Q142.       # citizenship: France (CountryDefinition's WikidataQid)
  ?player wdt:P54 wd:Q9617.      # member of sports team: Arsenal (ClubDefinition's WikidataQid) — any point in their career
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
}
```

1. Check `PlayerAttribute` first, as always — cache hit means no query at all.
2. On a cache miss: run the intersection query above directly, using the
   two QIDs already stored on `CountryDefinition`/`ClubDefinition` — one
   query answers the cell, full career history included, nothing extra to do.
3. Cache whatever players are returned into `PlayerAttribute`. Optionally
   (not required for Tier 0) also cache the club's full roster in one
   broader query for reuse across other country combinations — worth
   doing later as an optimization, not necessary now since Wikidata's
   query-time-based throttling (not a small daily count) means there's no
   real pressure to minimize query count the way there was with
   API-Football.
4. If no result: reject this combination, let REQ-101's existing retry
   logic pick a different one, same as always.

**The one-time setup cost** is genuinely just looking up ~15 QIDs for
clubs and ~15-20 for countries by hand — no bulk script, no API budget to
manage, done once before the first grid ever generates.

**Guessing**: plain text input, now with autocomplete suggestions (REQ-207,
`PlayerNameIndex`/ADR-0007/COMP-10) — pulled forward from Tier 1 and built,
`docs/backlog.md` S-032, 2026-07-17; see the Tier 1 section below for what
shipped. Autocomplete surfaces names only, never correctness (ADR-0007's
boundary rule), so nothing about grid difficulty changes just because
suggestions exist. Basic name normalization only — lowercase, strip
diacritics/punctuation (the simple half of REQ-208; defer the alias table
and fuzzy typo tolerance — REQ-208's guess-*scoring* path still doesn't
consult `PlayerNameIndex`, only autocomplete does). Disambiguation
simplified: if a guess matches multiple real players and any of them
satisfies the cell, accept it — no picker UI (REQ-209's full disambiguation
prompt — defer).

**Keep as-is** (these are cheap and matter even at MVP scale):
- REQ-210 (2 guesses per cell, locks immediately on correct)
- REQ-201–206 (submit guess, live uniqueness %, round close, total score) —
  this is the actual game, not overhead
- REQ-101/102/107 (grid generation, configurable size, no Country×Country)

**Accounts**: email + password via Supabase Auth, **no email confirmation
required to play** (REQ-701-705 — defer; turn off Supabase's
confirmation requirement in project settings). Resend integration
(ADR-0005) — defer entirely, not needed if nothing requires confirmation yet.

**Leagues**: global leaderboard (REQ-401). Custom leagues create/join
(REQ-402/403) pulled forward and built 2026-07-20, `docs/backlog.md`
S-063 — see the Tier 1 section below. REQ-404's full per-custom-league
leaderboard (tab switching, per-league reads) remains deferred.

**Not needed yet**: backups (REQ-901), failure alerting (REQ-902), legal
docs (`docs/legal/`) — none of these have real stakes until real users and
real data exist.

## Tier 1 — add only when real testing shows a specific need

Each of these was designed to solve a specific, real problem — but "this
could happen" isn't the same as "this is happening." The trigger for each
is written as something you can actually observe, not a vague feeling:

- **API-Football as a fallback source, plus expanding beyond the initial
  ~15 clubs** (ADR-0011's full waterfall — Tier 0 only implements the
  "try Wikidata" half) — trigger: you want more clubs than are worth
  manually looking up QIDs for one at a time, or a specific club/player
  turns out to have poor Wikidata coverage and needs a second source to
  fall back on. `ExternalApiUsage`'s shared daily budget tracking only
  matters once this is added — Tier 0 doesn't need it since Wikidata
  alone has no small daily cap to manage
- ~~**Guess-time live verification** (REQ-211, ADR-0010) — trigger: while
  reviewing rejected guesses (spot-check a sample occasionally during
  testing), you find one that was actually correct, more than a rare
  fluke~~ — **Trigger hit and pulled forward, 2026-07-10** (three genuinely
  correct guesses wrongly rejected on one live grid). Implemented in Tier 0
  without its `PlayerNameIndex` prerequisite — see ADR-0018 for why that's
  safe here and REQ-211's status note for what's still deferred (the
  API-Football fallback leg and budget-gating)
- ~~**Autocomplete + `PlayerNameIndex`** (REQ-207, ADR-0007) — trigger: you
  or a tester finds typing exact names tedious enough to mention it
  unprompted, not just "would be nice"~~ — **Pulled forward by deliberate
  choice, 2026-07-12, and built, 2026-07-17 (`docs/backlog.md` S-032).**
  The trigger itself never strictly fired (no unprompted complaint was
  ever observed) — building it now was chosen anyway. `PlayerNameIndex`
  (COMP-10) is populated via `PlayerNameIndexImporter`'s bulk,
  birth-year-sliced Wikidata query (`P106` = association football player;
  originally `LIMIT`/`OFFSET`-paged, replaced 2026-07-18 after every page
  timed out server-side in production — `implementation-document.md` §6a,
  NOTES.md 2026-07-18), run through the
  `import-player-name-index` CLI verb/workflow, `workflow_dispatch`-only,
  no schedule yet — exactly the mechanism ADR-0007 already specified, no
  new ADR needed, this is that ADR's own design being built. `GET
  /players/autocomplete?query=&limit=` queries `PlayerNameIndex` only,
  never `PlayerAttribute`/`PlayerOverride` (ADR-0007's boundary rule);
  `GuessInput.tsx` wires this into a debounced (275ms, 2+ characters)
  suggestion list, styled with neutral tokens only so a suggested name
  never implies correctness (see `docs/design-document.md`'s SCREEN-02
  implementation note). REQ-208's alias/fuzzy-typo-tolerance clause
  remains deferred — autocomplete alone (a correct-spelling suggestion
  list) is what got built, not typo tolerance for free-typed guesses — and
  REQ-209's disambiguation UI (below) remains deferred too
- ~~**Disambiguation UI** (REQ-209)~~ — **pulled forward and built
  2026-07-21, `docs/backlog.md` S-067**, ahead of the trigger firing (no
  real name collision was actually observed) — pulled forward by
  deliberate choice, same pattern as REQ-108/REQ-214/REQ-402-403's own
  precedent
- **Player photo on cell reveal** (REQ-214) — **Pulled forward by
  deliberate choice, 2026-07-18.** No trigger fired — there was no observed
  complaint or pain point; this was pulled forward because the idea was
  raised directly and judged worth doing now, recorded plainly rather than
  dressed up as a discovered need. Reads Wikidata's `P18` (image) through
  the correctness-side query REQ-101/102 already run and cache
  (`Player`/`PlayerAttribute`, COMP-06) — explicitly not a repeat of the
  `PlayerNameIndex.PhotoUrl` field built in S-032 and dropped 2026-07-18
  (`RemovePlayerNameIndexPhotoUrl` migration) once it turned out the
  autocomplete UI never displayed it; that column stays gone, and
  `PlayerNameIndex`/autocomplete (REQ-207, ADR-0007) is untouched by this
  item. See REQ-214 for the acceptance criteria, including the
  no-layout-change and no-broken-image-icon constraints. **Built,
  2026-07-18** (`docs/backlog.md` S-043 backend, S-044 frontend) — see
  ADR-0028 for the `Player.PhotoUrl` (not `PlayerAttribute`) placement
  decision made along the way.
- ~~**Trophy category** (REQ-108, plus `CountryDefinition`/`ClubDefinition`'s
  full external-ID resolution, ADR-0012) — trigger: Country×Club has been
  played enough rounds that it feels repetitive, a subjective call but one
  you'll notice by just playing it yourself for a couple of weeks~~ —
  **Trigger judged hit, 2026-07-12**, after two weeks/29 stories' worth of
  real play. **Built 2026-07-20, `docs/backlog.md` S-031**, deliberately
  scoped narrower than REQ-108's full definition: **individual awards only
  for v1** (Ballon d'Or), which map to Wikidata's `P166` ("award received")
  — the same simple query shape as the existing Country×Club intersection
  query. Team-competition trophies (World Cup, Champions League) need a
  genuinely different query pattern (squad membership + tournament result
  — no single property links a player directly to "won this tournament")
  and stay explicitly deferred to a follow-up story, not folded into S-031.
  Two things worth knowing about what actually shipped, not just that it
  did: (1) Ballon d'Or's QID was a training-knowledge guess, not verified
  against a live Wikidata page (same sandbox limitation that bit S-036) —
  needs a human check before real reliance; (2) with only this one trophy
  seeded, every Trophy pairing is currently infeasible for any realistic
  grid size, so Trophy is mechanically wired up but won't actually be
  selected until more trophies are added as reference data — exactly the
  "a data change, not a code change" growth path REQ-108 was designed for.
- ~~**National teams as distinct footballing entities** (England, Scotland,
  Wales, Northern Ireland) — trigger: "United Kingdom" as a category
  starts feeling wrong/generic for football trivia, or you specifically
  want the England card back~~ — **Pulled forward by explicit product
  decision and built, 2026-07-21 (REQ-114/ADR-0035)**, ahead of the
  trigger firing, same pattern as REQ-108/REQ-214/REQ-402-403's own
  precedent. `CountryDefinition` gained a `UsesCountryForSportProperty`
  flag (default `false`); England/Scotland/Wales/Northern Ireland are
  seeded as four additional `CountryDefinition` rows (never replacing
  United Kingdom) with the flag `true`, queried via Wikidata's `P1532`
  ("country for sport") through a second `WikidataClient` query path,
  never a replacement for the `P27` path every other country uses — see
  ADR-0035 for why a per-row flag was chosen over a separate category
  type, and REQ-114's status note for the unverified-QID caveat (`Q21`/
  `Q22`/`Q25`/`Q26`, not checked against a live Wikidata page this
  session).
- **Creating a real "prod" environment** (ADR-0006, ADR-0009, REQ-801-804's
  full test-data API) — trigger: you have at least one real user who isn't
  you, or you find yourself nervous about testing a change directly
  against your only (dev) environment. This is also the trigger for
  backups and alerting below — they don't make sense before prod exists.
  The sync/promote scripts already exist (ADR-0009); this step is
  "provision the second environment," not "build the tooling"
- **Backups + alerting** (REQ-901/902) — trigger: before inviting anyone
  beyond yourself to play — this is a bright line, not a judgment call,
  once real people's accounts/scores exist
- **Email confirmation + Resend** (REQ-701-705, ADR-0005) — trigger:
  opening the game to anyone you don't personally know/trust
- ~~**Custom leagues** (REQ-402-404)~~ — **create/join (REQ-402/403)
  pulled forward and built 2026-07-20, `docs/backlog.md` S-063**, ahead of
  the trigger firing (no request was actually observed — pulled forward by
  deliberate choice, same pattern as REQ-108/REQ-214's own precedent).
  REQ-404's full per-custom-league leaderboard remains deferred — trigger:
  someone actually wants to see a ranked view scoped to one custom league,
  not just membership
- **Legal docs finalized** (`docs/legal/`) — trigger: before any public
  launch to strangers — also a bright line, not a judgment call
- **Guest play** (REQ-717, ADR-0036) — trigger: none fired; **pulled
  forward by deliberate product decision, 2026-07-21**, same pattern as
  REQ-108/214/402-403's own precedent (no observed request — the product
  owner reasoned through it directly with the goal of a better uniqueness
  signal via more players guessing). **Backend implemented, 2026-07-21**
  (`POST /auth/guest`, `POST /auth/claim`, `User.IsGuest`/`User.ClaimedAt`,
  REQ-409's qualifying-rounds query narrowed for both) — a real,
  auto-provisioned `User` row with no email/password, participating
  unmodified in guessing/scoring/uniqueness and round-scoped leaderboards
  REQ-406/407/408, excluded from REQ-409's all-time median ranking, its own
  tighter rate limit (`auth-guest`, 3/min default), and a claim/upgrade path
  to a real account preserving guess history. **Frontend implemented,
  2026-07-21 (S-070):** a "Play as guest" entry point on the login/signup
  screen and a "Save your progress" claim section in Settings, both wired
  to the two endpoints above, plus a small header nudge while the session
  is a guest — see `docs/backlog.md`'s S-070 entry for what was built. A
  same-day follow-up (backend `MeResponse.IsGuest` field + a matching
  frontend switch-over) closed the one gap S-070 flagged back (the
  frontend originally derived guest status from a nullable email instead
  of a real field) — see that entry's own addendum. REQ-717/ADR-0036's
  frontend half is therefore complete end-to-end, closing out this Tier
  1/2 pull-forward. **Captcha hardening (2026-07-21, ADR-0037): backend
  half built 2026-07-22, frontend not yet built.** Cloudflare Turnstile
  added as a second, complementary abuse-prevention layer alongside the
  existing `auth-guest` rate limit, scoped to `POST /auth/guest` only — see
  REQ-717's "Bot-check (captcha) for guest creation" addition for the
  acceptance criteria and `SETUP.md`'s Supabase section (step 5) for the
  new manual precondition (a Cloudflare Turnstile site, plus enabling
  Supabase's captcha setting). `AuthController.Guest`/
  `SignInAnonymouslyAsync` now thread a `CaptchaToken` through to
  Supabase's own verification and return a distinct rejection on failure;
  the frontend's Turnstile widget/token acquisition (obtaining a token
  before calling `POST /auth/guest`, resetting the widget on the distinct
  rejection) is the remaining piece before this is complete end-to-end.

## Tier 2 — already deferred, unchanged

- Club crests (`ClubCrest`, ADR-0008) — Phase 2, as already documented
- Round-result notification emails (REQ-706) — Phase 2, as already documented

## What this means for the other docs

**The concrete implementation order for Tier 0 lives in `docs/backlog.md`**
(stories S-001 through S-013, each one session/PR-sized, each leaving the
system deployable and testable) — this file defines the scope, that file
defines the sequence.

`requirements-document.md`, `architecture-document.md`, and
`implementation-document.md` still describe the full system accurately —
they're the reference for what a requirement or component eventually does,
not a claim that it's all being built right now. Treat every REQ/ADR
number in Tier 1/2 above as "designed, not yet built" until this document
says otherwise. When a Tier 1 item actually gets built, update this file
to move it into a new "Tier 0 (current)" or similar, so this document
keeps reflecting reality rather than becoming stale itself.
