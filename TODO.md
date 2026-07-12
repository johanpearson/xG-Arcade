# TODO — xG Arcade

A single consolidated checklist, ordered around **`MVP-SCOPE.md`** — build
Tier 0 first, and most of the setup below shrinks accordingly. Everything
here is also documented in context elsewhere (linked below); this file
exists so nothing gets lost between scattered ADRs and READMEs. Update it
as items complete or new ones surface; don't let it silently go stale.

## Before writing any code (MVP-scoped)

- [ ] Pick a real name (currently "xG Arcade" is confirmed — if this
  changes again, it's a clean find-and-replace per the ADR-0003 boundary design)
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
  ADR-0022's round-closing fix shipped, it needs one extra
  `generate-round.yml` cron cycle to get closed automatically — or force it
  immediately via `POST /internal/test-data/force-close-round/{roundId}`
  (non-Production only) so its score reaches the leaderboard right away
  rather than waiting.

## Tier 1 — revisit only after real testing shows a specific need

See `MVP-SCOPE.md`'s Tier 1 section for the full list and the reasoning
per item. Don't work through this as a checklist to complete — each item
should be triggered by an actual observed problem, not by this list existing:

- [ ] API-Football as a fallback source + expanding beyond ~15 clubs
  (only once you want more clubs than manual QID lookup is worth, or hit
  poor Wikidata coverage for a specific club/player)
- [ ] Guess-time live verification (only if a correct guess actually gets
  wrongly rejected in practice)
- [ ] Autocomplete + `PlayerNameIndex` (only if blind typing is actually annoying)
- [ ] Disambiguation UI (only if a real name collision actually happens)
- [ ] Trophy category + full `CountryDefinition`/`ClubDefinition` external-ID resolution
- [ ] Create a real "prod" environment (dev already exists from Tier 0) —
  bidirectional sync, test-data API
- [ ] Backups + failure alerting
- [ ] Email confirmation + Resend
- [ ] Custom leagues
- [ ] Legal docs finalized (required before any real public launch, not optional)

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
