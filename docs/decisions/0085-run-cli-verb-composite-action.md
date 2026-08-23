# ADR-0085: Extract `.github/actions/run-cli-verb` as a composite action, not a reusable workflow

- **Status:** Accepted
- **Date:** 2026-08-23
- **Related requirements:** none (CI/infra tooling only, no REQ affected)
- **Related components:** none (no `XGArcade.*` component boundary changed)

## Context

S-175 (`docs/backlog.md`, Epic 24) confirmed 8 workflow sites repeating the
same "checkout, setup-dotnet, run a CLI verb, connect to dev DB" shape:
`backfill-player-photos.yml`, `import-player-name-index.yml`,
`prefetch-player-careers.yml`, `purge-game-history.yml`,
`purge-player-pool.yml`, `warm-grid-cache.yml`, and `deploy.yml`'s
`migrate-and-seed-database` job — 7 genuine sites, differing only in which
CLI verb runs, whether it takes a confirmation argument, each workflow's own
`timeout-minutes`, and (for `warm-grid-cache.yml` only) a 2-attempt retry
loop around the run step. GitHub Actions offers two ways to share this: a
composite action (`.github/actions/<name>/action.yml`, invoked via
`uses: ./...` from a step) or a `workflow_call` reusable workflow (invoked
via `uses: ./...` from a job, called with `jobs.<id>.with`/`secrets`).

**Investigation finding, not assumed from the backlog text:** the 8th site
the backlog listed, `ci.yml`'s "Migrate + seed local database" step, turned
out on inspection not to match this shape at all — it shares its job's own
checkout/setup-dotnet with unrelated frontend/Playwright steps rather than
owning a standalone 4-step block, and its connection string is a hardcoded
local ephemeral Postgres container, never
`secrets.DEV_DATABASE_CONNECTION_STRING`. It is left unconverted, with a
comment at the site explaining why (see `ci.yml`). This ADR and S-175's
"Built as" note cover 7 real call sites, not 8.

This is the same "could reasonably have gone another way" choice ADR-0072
(splitting `generate-round.yml` into per-`GameKey` files) already reasoned
about for a sibling problem, so this decision deliberately mirrors that
one's independence argument rather than re-deriving it from scratch.

## Decision

Extract `.github/actions/run-cli-verb/action.yml`, a **composite action**
taking `verb` (required), `arg` (optional, single extra argv element),
`connection-string` (required), and `attempts` (optional, default `1`) as
inputs. It runs `actions/setup-dotnet@v6` (`dotnet-version: "10.0.x"`) then
`dotnet run --project backend/src/XGArcade.Api -- <verb> [<arg>]` against
`ConnectionStrings__Database=<connection-string>`, with `attempts > 1`
reproducing `warm-grid-cache.yml`'s existing retry/`::warning::`/`::error::`
shape byte-for-byte; `attempts` at its default of `1` runs the command once
with no synthetic annotations, so the other 6 sites' failure output is
unchanged from before.

Each of the 7 real call sites keeps its own `actions/checkout@v7` step
before calling the composite action — this is not optional boilerplate left
behind by an incomplete extraction. A workflow step referencing a local
composite action (`uses: ./.github/actions/run-cli-verb`) needs the
repository already checked out for the runner to resolve that action's own
`action.yml` from the filesystem; folding checkout into the composite action
itself would be circular (the very first internal step would need the
checkout it's trying to perform). This is standard, documented GitHub
Actions behavior for local composite actions, not specific to this repo.

Each caller's own `on:`/cron/`timeout-minutes` stays untouched in its own
file — the composite action only replaces the `setup-dotnet` + run +
connection-string-wiring steps.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Composite action (chosen) | Each `.yml` file's `on:`/cron/`timeout-minutes` stays fully independent, matching ADR-0072's per-workflow-independence reasoning; smaller behavioral surface (an action, not a second workflow with its own trigger semantics); trivial to smoke-test one call site without touching the others | `actions/checkout` can't be folded in (see above) — every call site still has one shared line | Matches this codebase's existing preference (ADR-0072) for genuinely independent workflow files over shared/centralized workflow machinery, and the checkout duplication is a small, well-understood cost, not a functional gap |
| `workflow_call` reusable workflow | Could in principle also centralize `on.schedule`/cron definitions | Centralizes more than this story asked for; needs explicit `secrets: inherit` or per-secret passing at every call site (a bigger, easier-to-get-wrong behavioral surface than composite-action `with:` inputs); a reusable workflow is itself a second workflow run nested inside the caller's run, changing what the Actions tab shows (a "called workflow" entry, not the caller's own steps) — harder to satisfy this story's "identical Actions-tab run" acceptance criterion | Bigger surface for no benefit this story needs; risks failing the story's own "identical run" bar by changing what the Actions tab displays, not just how the YAML is authored |

## Consequences

- Positive: the checkout+setup-dotnet+run+connection-string shape now lives
  in one file (`.github/actions/run-cli-verb/action.yml`); a future change
  to the .NET version pin, the `dotnet run` invocation shape, or the retry
  annotation wording lands once instead of in up to 7 places.
- Positive: `warm-grid-cache.yml`'s retry logic is now expressed as
  `attempts: '2'` rather than duplicated inline bash — the only one of the
  7 sites that ever needed retries stays the only one paying for that
  complexity.
- Negative / trade-off accepted: `actions/checkout@v7` remains a literal
  duplicated line in all 7 call sites — a genuine GitHub Actions constraint
  (see Decision), not something a future refactor should try to remove.
- Follow-up: `ci.yml`'s "Migrate + seed local database" step was
  deliberately left unconverted (different connection string source, not a
  standalone 4-step block) — do not fold it into this composite action
  without first changing what it actually does (i.e. pointing it at the dev
  DB), which is out of this story's scope.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

Specifically:

- Do not remove the per-call-site `actions/checkout@v7` step and try to
  fold checkout into `run-cli-verb`'s own composite steps — this is a real
  GitHub Actions constraint (a local composite action's own `action.yml`
  must already be resolvable from a checked-out workspace before its steps
  can run), not leftover duplication to clean up.
- Do not convert `ci.yml`'s "Migrate + seed local database" step to use
  this composite action without first deliberately changing its connection
  string away from the local ephemeral Postgres container — doing so
  silently would point CI's E2E migrate/seed step at the real dev database,
  which is not what that step is for.
- A new single-CLI-verb dev-DB maintenance workflow should call this
  composite action rather than hand-rolling the 4-step shape again.
