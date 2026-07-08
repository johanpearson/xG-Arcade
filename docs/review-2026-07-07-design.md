# Design & Plan Review — 2026-07-07 (second pass)

A design/architecture review of the full doc set (distinct from the same
day's earlier file-quality review, `review-2026-07-07.md`). Goal: find
real flaws in the *plan* — contradictions, ambiguities, and gaps that
would surface as bugs or blocked work during implementation. All findings
below were **fixed the same day**; this file records what was wrong and
why, for later revisiting.

## Findings (all ✅ fixed)

**1. ci.yml was structurally broken for Tier 0 — the biggest finding.**
E2E depended on a `deploy-dev` job needing a dev environment that doesn't
exist in Tier 0, and reset test data via an API that is both Tier 1 and
excluded from prod by design. CI would have been red from the first
commit, with no valid way to make it green inside Tier 0's own rules.
*Fix:* rewrote `ci.yml` to a Tier 0 shape — E2E runs against a local stack
composed inside the workflow (Postgres service container + API from
source + Playwright's own webServer for the frontend). The Tier 1 shape
(deploy-dev + deployed-env E2E + test-data reset) is documented in the
workflow header and `infra/README.md`, restored when dev exists. Backlog
S-002/S-013, `SETUP.md`, and `implementation-document.md` aligned.

**2. REQ-204's uniqueness formula counted wrong guesses.** As written,
incorrect guesses and burned attempts entered the denominator — scores
would have been distorted by how much *failing* happened on a cell, which
has nothing to do with answer rarity, and REQ-210's two-attempt system
would have double-counted players. *Fix:* denominator is now explicitly
correct guesses only, one per player; also pinned the Tier 0
multi-fit-disambiguation case to a deterministic PlayerId so identical
guesses always group as the same answer.

**3. `Player` had no dedup identity.** Two intersection queries returning
the same player (France×Arsenal, Brazil×Barcelona) would have created
duplicate rows, corrupting uniqueness grouping and stats. *Fix:*
`Player.WikidataQid` with a unique index and an explicit upsert-by-QID
rule (entity comment + §6a + backlog S-006 acceptance).

**4. Nothing forbade `LIMIT` on the intersection query.** The query's
completeness is load-bearing: it's exactly what makes Tier 0's cache-only
guess checking fair *without* guess-time live verification (REQ-211,
Tier 1). A LIMIT added for "performance" would silently reintroduce the
correct-guess-marked-wrong bug. *Fix:* explicit no-LIMIT rule in §6a and
S-006, with the reasoning attached so it isn't "optimized" away.

**5. Free alias matching was being left on the table.** Tier 0 defers the
alias system, but Wikidata's `skos:altLabel` ships curated
nicknames/alternate names in the same query for one extra SELECT column —
most of REQ-208's alias value at zero curation cost. *Fix:* fetch
altLabels into `PlayerAlias` as part of S-006.

**6. A silently failed round generation = dead app.** Tier 0 has no
alerting (REQ-902 is Tier 1), and generation ran just-in-time, so an
abort meant no round until someone happened to check. *Fix:* REQ-301 now
generates one round ahead — a failure has a full round-length window to
be noticed. Backlog S-008 updated.

**7. "Admin" was undefined.** S-012 built admin endpoints with no stated
authorization mechanism. *Fix:* config-based `Admin__UserIds` env var
(simplest thing that works solo), documented at the JWT-validation point
in `implementation-document.md` §4 and in S-012's acceptance.

**8. "Country" semantics were ambiguous.** Citizenship (P27) vs "capped
for the national team" give different answers for naturalized/dual
players and *will* be disputed by players eventually. *Fix:* documented in
§6a as a deliberate rule: Country = citizenship.

## Judged and left alone (so this isn't relitigated)

- Prod-only deploys on every merge to `main` during Tier 0: acceptable —
  deploys don't touch game data, and Container Apps updates roll.
- `INTERNAL_JOB_TOKEN` bearer auth for `/internal/*`: fine at this scale
  over HTTPS; revisit alongside Tier 1 hardening.
- ~~Wikidata P54 including national/youth teams: harmless~~ — **superseded,
  2026-07-07 (later same day):** "senior career only" was decided after
  this review was written. Querying the senior club's specific QID
  excludes youth appearances *when* that club's youth setup has its own
  distinct Wikidata item, which isn't guaranteed for every club/player —
  a thin or poorly-maintained page could record a youth-only spell
  directly against the senior club's QID with no distinction. Accepted as
  a known Tier 0 data-quality limitation, mitigated by the existing
  manual override (S-012), not a new filtering mechanism — see S-006.
- REQ-204 computing on read: fine at MVP scale; indexes already specified.

## Standing conclusion

With these fixes, the plan is internally consistent end-to-end: every
backlog story is executable within Tier 0's own constraints, the scoring
model is well-defined, and the two known deliberate risks (no backups, no
alerting until Tier 1) have documented mitigations or bright lines.
Revisit this review after S-013 — the remaining unknowns (Wikidata data
quality per club, WDQS latency in practice) can only be answered by
running it.
