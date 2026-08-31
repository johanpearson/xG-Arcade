# TODO — xG Arcade

A single consolidated checklist, ordered around **`MVP-SCOPE.md`** — build
Tier 0 first, and most of the setup below shrinks accordingly. Everything
here is also documented in context elsewhere (linked below); this file
exists so nothing gets lost between scattered ADRs and READMEs. Update it
as items complete or new ones surface; don't let it silently go stale.

## Before writing any code (MVP-scoped)

- [x] Pick a real name — "xG Arcade" is final (if this ever changes, it's
  a clean find-and-replace per the ADR-0003 boundary design)
- [ ] Create accounts: GitHub repo, **one** Azure subscription (`xg-arcade-dev-rg`),
  **one** Supabase project (this is "dev," Tier 0's only environment).
  **Not needed yet for MVP:** API-Football (Tier 1
  fallback source), a second (prod) Supabase project, Resend — see
  `MVP-SCOPE.md`'s Tier 0 vs Tier 1
- [ ] Set up Azure OIDC federated credential for GitHub Actions
  (`AZURE_CLIENT_ID`/`TENANT_ID`/`SUBSCRIPTION_ID` secrets) — see `infra/README.md`
- [ ] Set the MVP-scoped subset of GitHub Actions secrets (skip
  `DEV_*`-prefixed ones and `RESEND_API_KEY` until Tier 1) — see `infra/README.md`
- [ ] Turn off Supabase Auth's "confirm email" requirement in project
  settings — MVP doesn't require email confirmation to play

## First Claude Code session

- [ ] Read `MVP-SCOPE.md` before anything else, then work `docs/backlog.md`
  story by story (S-001 onward) — one story per session/PR
- [ ] Follow `CLAUDE.md`'s "Getting started" checklist: solution/projects →
  Dockerfile → frontend scaffold → one trivial end-to-end slice through
  the whole CI/CD pipeline, *before* real feature work — scoped to Tier 0
  only (Wikidata client only — no API-Football yet, no Trophy category, no dev/prod split)
- [ ] Confirm `ci.yml` passes on the empty/trivial scaffold
- [ ] Confirm `deploy.yml` successfully deploys the trivial slice

## S-013 follow-ups (need real network access, not doable from this sandbox)

- [ ] Manual smoke test of login → guess → score against the deployed dev
  URL (`DEV_BACKEND_HOSTNAME`/`DEV_FRONTEND_HOSTNAME`) — S-013's own
  acceptance criterion; this sandbox has no route to the deployed
  environment
- [ ] Spot-check a sample of real rejected guesses on the deployed dev
  environment once it has real play history — seeds `MVP-SCOPE.md`'s
  Tier 1 triggers (disambiguation UI, autocomplete). This sandbox's
  network policy blocks `wikidata.org` (same limitation NOTES.md already
  records for S-006/ADR-0017), so it also has no real guesses to sample.

## S-029 follow-up (one-time, after this fix deploys)

- [ ] If any round in the deployed dev environment already ended before
  ADR-0022's round-closing fix shipped, it needs one extra cron cycle of
  that round's own `GameKey`-specific workflow (`generate-grid-round.yml`
  for `xg-grid`, `generate-path-round.yml` for `xg-path` — split from the
  single `generate-round.yml` as of S-136/ADR-0072) to get closed
  automatically — or force it immediately via
  `POST /internal/test-data/force-close-round/{roundId}`
  (non-Production only) so its score reaches the leaderboard right away
  rather than waiting.

## S-136 follow-up (one-time, after this split deploys)

- [ ] Field-verify ADR-0072's `workflow_dispatch` isolation claim against the
  deployed dev environment: manually dispatch `generate-grid-round.yml` with
  a `round_duration_hours` override and confirm the next `xg-path` round's
  `RoundDuration` is unaffected (then the inverse for
  `generate-path-round.yml`). Not yet possible from this sandbox — no path
  to trigger a real GitHub Actions dispatch — so this is code-reviewed but
  not field-verified until done.

## xG Predict / football-data.org wiring follow-up (blocking `xg-predict` round generation)

**Superseded 2026-08-31 (ADR-0099):** API-Football was tried first
(ADR-0094) but its free tier turned out to restrict season access to a
rolling historical window that excludes the current season entirely
(confirmed via api-football.com's own support chat) — structurally
unusable for a live prediction game without a paid plan. Swapped for
football-data.org instead, whose free tier explicitly includes the current
Premier League season. The `API_FOOTBALL_API_KEY` GitHub secret, if one was
ever set, is no longer read by anything and can be deleted; nothing in the
codebase references API-Football for xG Predict any more (xG Grid's own,
separate, still-dormant Tier 1 API-Football fallback per ADR-0011 is
unaffected and unrelated).

- [ ] **Sign up for football-data.org's free tier** and grab the API token
  (`SETUP.md` §4) — needs a real human/account, not doable from this
  sandbox. Until this is done, `generate-predict-round.yml` will keep
  failing every run with `football-data.org is not configured on this
  environment yet.`
- [ ] Set the token as the `FOOTBALL_DATA_API_KEY` GitHub Actions repository
  secret (`infra/README.md`) — `deploy.yml`/`main.bicep`/
  `backend-container-app.bicep` thread it through to the Container App's
  `FootballData__ApiKey` env var automatically on the next deploy; no
  further code change needed once the secret is set.
- [x] **Read football-data.org's actual terms of service** — done
  2026-08-31: the product owner retrieved the real terms directly (this
  sandbox's egress proxy still blocks `football-data.org`/
  `docs.football-data.org`) and pasted them in; saved at
  `docs/decisions/correspondence/football-data-org-terms.md`. No
  commercial-use restriction found (ADR-0099's Decision item 4 status
  update has the full analysis). One real caveat: §9.1 says historical
  data can't keep being referenced after the subscription is cancelled —
  not a blocker while active, but re-check before ever letting the account
  lapse.
- [ ] Add the required "Football data provided by the Football-Data.org
  API" attribution somewhere in the frontend (footer, per
  `docs/design-document.md`'s token system) before public launch — a real
  ToS requirement, not optional polish.

## Tier 1 — revisit only after real testing shows a specific need

See `MVP-SCOPE.md`'s Tier 1 section for the full list and the reasoning
per item. Don't work through this as a checklist to complete — each item
should be triggered by an actual observed problem, not by this list existing:

- [ ] API-Football as a fallback source + expanding beyond ~15 clubs
  (only once you want more clubs than manual QID lookup is worth, or hit
  poor Wikidata coverage for a specific club/player)
- [x] Guess-time live verification — built 2026-07-10, ADR-0018
- [x] Autocomplete + `PlayerNameIndex` — pulled forward 2026-07-12, built
  2026-07-17, `docs/backlog.md` S-032
- [ ] Disambiguation UI (only if a real name collision actually happens)
- [x] Trophy category, individual-awards-only v1 (Ballon d'Or) — pulled
  forward 2026-07-12, built 2026-07-20, `docs/backlog.md` S-031; full
  `CountryDefinition`/`ClubDefinition` external-ID resolution and
  team-competition trophies remain genuinely deferred
- [ ] Create a real "prod" environment (dev already exists from Tier 0) —
  bidirectional sync, test-data API
- [ ] Backups + failure alerting
- [ ] Email confirmation + Resend
- [ ] Custom leagues
- [ ] Legal docs finalized (required before any real public launch, not optional)
- [x] In-app incident reporting to GitHub Issues (REQ-903, ADR-0064) — pulled
  forward and built 2026-08-10 (`POST /incidents`, `Core.IncidentReporting`,
  a footer-accessible "Report a problem" button opening a modal, reachable
  from any screen — moved out of Settings the same day). The
  `INCIDENT_REPORT_PAT` secret (see `SETUP.md` step 6 for exact scopes,
  `infra/README.md` for the full wiring) has now been created. Screenshot
  attachment was requested and deliberately deferred — see SCREEN-11
  (`docs/design-document.md`) for why (no GitHub API for it without
  widening the PAT's scope or adding a third-party image host).
- [ ] Do the one required manual end-to-end test of incident reporting
  against a throwaway repo (not this one) before relying on it in
  production (REQ-903's "Test level" note) — the real secret is set now,
  this hasn't been verified yet

## Before public launch (Tier 1 — not MVP-blocking)

- [ ] **Email API-Football** for written confirmation that this project's
  use (gameplay product, permanent caching, not resold) is acceptable
  under their terms — ADR-0008. Draft ready at
  `docs/decisions/correspondence/api-football-confirmation-email.md`.
  Worth doing early even though it's Tier 1 — it's a five-minute email,
  not a redesign, and cheaper to send now than to remember later.
- [ ] Get `docs/legal/privacy-policy-draft.md` and
  `docs/legal/terms-of-service-draft.md` reviewed by a qualified
  professional, or run them through a generator (Termly/TermsFeed/GetTerms)
- [ ] Test the backup restore procedure manually at least once
  (once backups are built — REQ-901)
- [ ] Confirm GitHub Actions failure-notification emails actually arrive
  (once alerting is built — REQ-902)
- [x] **Run the `purge-game-history` clean slate** (`docs/backlog.md` S-152,
  Epic 16). Done 2026-08-18 via `purge-game-history.yml`: 29 `Round`, 230
  `Guess`, 4 `PlayerSuggestion`, 18 `GridInstance` (+162 `GridCell`), 11
  `PathInstance` (+4 `PathPuzzle`), 4 `PathCycleTargetUsage`, and the
  `PathTargetCycle` singleton row wiped — see `NOTES.md`'s matching entry
  for the full row-count record. `Player`/reference-table data untouched,
  as designed.
  Record the actual row counts wiped in `NOTES.md` when this runs, per
  `purge-player-pool.yml`'s own incident-log discipline.

## Known open design questions (not blocking, revisit when relevant)

- [ ] Whether a dark theme is ever offered as a user preference
  (`docs/design-document.md` §7)
- [ ] Whether the badge-dock reveal animation performs acceptably on
  low-end mobile once built, or whether the reduced-motion fallback should
  become the default (`docs/design-document.md` §7)

## Deferred to Phase 2 (Tier 2 — designed, not built)

- [ ] Round-result notification emails (REQ-706)
- [ ] Real club crest imagery via API-Football (`ClubCrest` entity, ADR-0008)

## Ongoing discipline (not a one-time task)

- [ ] Run `doc-sync` / `/update-docs` at the end of coding sessions —
  don't let docs drift from reality
- [ ] Keep `MVP-SCOPE.md` current — when a Tier 1 item actually gets
  built, move it out of Tier 1 so this file doesn't go stale itself
- [ ] New external data sources get a terms-of-service check before
  integration, same as ADR-0008 did for API-Football
- [ ] New structural decisions get an ADR, not just a code change
