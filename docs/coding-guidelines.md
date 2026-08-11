---
doc_id: coding-guidelines
title: Coding Guidelines
version: "0.7"
status: draft
last_updated: 2026-08-11
owner: Johan
related_docs:
  - architecture-document.md
  - implementation-document.md
update_when:
  - "A recurring code-review comment suggests a convention is missing"
  - "A new pattern is adopted that should apply project-wide"
---

# Coding Guidelines

> **For AI agents:** `quality-architect` checks against this document
> specifically, and owns its evolution (a recurring review comment should
> become a guideline here). If you're about to write code and something
> here contradicts what you were about to do, follow this document — if
> you think the guideline itself is wrong, say so explicitly rather than
> silently working around it.

## General principles

- **Composition over conditionals.** Prefer small, composable pieces
  (interfaces, strategy objects, separate methods) over branching logic
  that grows a single method's complexity. This applies to application
  code the same way it already applies to the IaC (Bicep modules).
- **Testability drives structure, not the other way around.** If a piece
  of logic is hard to unit test, that's usually a sign it's doing too much
  or reaching too many dependencies directly — restructure it, don't just
  add more mocks.
- **Small, focused units.** A class/component/function should have one
  reason to change. If describing what something does requires "and," it's
  probably two things.
- **Explicit over implicit.** No hidden side effects, no "magic" behavior
  that isn't visible from the method signature or an explicit comment.

## C# / backend

- **Nullable reference types enabled project-wide.** A `string?` vs
  `string` distinction is meaningful — don't suppress warnings with `!`
  without a comment explaining why it's actually safe.
- **Async all the way down.** No blocking calls (`.Result`, `.Wait()`) on
  async code — this causes deadlocks in ASP.NET Core specifically.
- **EF Core**: query through repositories/services that encapsulate the
  `DbContext`, not directly from controllers. This is what makes
  boundary rules like COMP-06's "only path to PlayerData" actually
  enforceable in code, not just in documentation.
- **EF Core writes: load-then-`SaveChangesAsync` through the change
  tracker, not `ExecuteUpdateAsync`/`ExecuteDeleteAsync`.** The latter
  translate to bulk SQL that EF Core's InMemory provider (used throughout
  this codebase's unit tests, §7) cannot translate, so they'd fail only in
  tests, not in production — a trap that's easy to miss in review. Established
  by S-025's `IUserRepository.DeleteAsync`/`IGuessRepository.AnonymizeByUserIdAsync`/
  `ILeagueRepository.RemoveMembershipsByUserIdAsync`; follow the same pattern
  for any future bulk-style repository write.
- **DTOs at the API boundary, domain entities internally.** Controllers
  accept/return DTOs; domain entities (the ones in
  `implementation-document.md`'s data model) never get serialized directly
  to API responses — this avoids accidentally leaking a field added for
  internal use.
- **Errors as problem-details responses** (per `architecture-document.md`
  §7), not raw exception messages leaking to the client. Log the full
  exception server-side; return a client-appropriate summary. **Narrow
  exception:** an `/internal/*` endpoint whose only caller is a
  bearer-token-gated scheduled job (never a public or player-facing
  client) may return a caught exception's own `Message` as the `detail` —
  the "client" reading it is the job's own CI log, not an untrusted
  surface, and REQ-902's failure alerting is Tier 1 (not built yet), so
  this is what makes a failed scheduled job diagnosable at all without
  direct server log access. This does not extend to any endpoint reachable
  by a player or the frontend, gated or not — see `InternalRoundEndpoints.cs`'s
  `/internal/generate-round` for the one endpoint currently relying on
  this, and `GuessEndpoints.cs`/`AdminEndpoints.cs` for the default rule
  still applying everywhere else.
- **External-client error contracts: swallow-to-empty is only valid where
  failure and no-data must be treated identically.** A "never throws,
  returns `[]`" client method is the right shape for an interactive path
  that genuinely wants a failure to look like a no-match (e.g. REQ-103's
  "never block grid generation on a Wikidata failure" — see
  `WikidataClient`'s intersection queries). It is the *wrong* shape for a
  batch/bulk job whose success metric IS the row count: there, a swallowed
  failure is indistinguishable from end-of-data, and a total outage becomes
  a silent exit 0. Batch-path client methods must throw on
  timeout/HTTP/parse failure (a distinct exception type, e.g.
  `WikidataQueryException`) so the job can retry and ultimately fail
  loudly — an empty result must mean exactly "no data," never "the call
  failed." Trigger: the S-032 `import-player-name-index` incident, where
  every WDQS page query timed out server-side, the swallow-to-`[]` contract
  read the timeout as end-of-data, and the job exited 0 having imported
  nothing (NOTES.md 2026-07-18). When one client serves both kinds of
  caller, give each path its own contract and document the split at the
  method level rather than averaging them into one.
- **Naming**: `PascalCase` for types/methods/properties, `camelCase` for
  locals/parameters, per standard .NET convention — no project-specific
  deviation here.

## TypeScript / frontend

- **Function components with hooks**, no class components.
- **Co-locate a component with its styles and tests** — don't split a
  single component's concerns across distant folders by file type.
- **Props are explicitly typed**, never `any`. If a prop's shape is
  genuinely dynamic, use a discriminated union, not `any`.
- **No prop drilling past 2 levels** — reach for context or a small store
  instead. This is a judgment call, not a hard rule; use it to catch
  actual pain, not as a reason to add state management prematurely.
- **Every color/font/spacing value traces to `design-document.md`'s token
  table** — this is enforced by `ui-implementer`, but applies regardless of
  which agent or person writes the code.
- **Fetch-on-mount sections that classify a result into "escalate on 401 /
  hide on 403 / show inline on any other error", guarded against unmount:**
  reuse `useAdminSectionFetch` (`frontend/src/admin/useAdminSectionFetch.ts`)
  rather than hand-rolling another `cancelled`-flag `useEffect`. Extracted once this
  exact shape reached five independent copies in that one file
  (`PlayerSuggestionsEntry`/REQ-512, `IncidentReportsEntry`/REQ-904,
  `AnnouncementBannerSection`/REQ-511, `AccountMetricsSection`/REQ-507,
  `XGPathCycleSection`/REQ-1209) — a rule-of-three-plus case flagged during
  REQ-512's quality gate and acted on once REQ-904 crossed the threshold. The
  hook owns only the transport half (fetch/cancel/401-escalate/403-hide/
  thrown-error-inline) and exposes `{ data, hidden, loadError, refetch }`; a
  business-level state carried inside a *successful* response (e.g.
  `XGPathCycleSection`'s `hasData`, `IncidentReportsEntry`'s `available`)
  stays the caller's to branch on via `data`, never folded into the hook —
  see that hook's own doc comment for why conflating the two would be wrong.
  Not admin-screen-specific in principle; if this shape shows up outside
  `AdminScreen.tsx`, that's `quality-architect`'s call on whether to promote
  it to a shared location, not a reason to duplicate it again first.

## Testing

- **Name tests after the requirement they verify**:
  `REQ###_MethodOrBehaviorUnderTest_ExpectedOutcome` (backend), a
  REQ-prefixed description string (frontend) — already established in
  `implementation-document.md` §7, repeated here because it's the single
  most important convention for keeping requirements and tests traceable.
- **Arrange/Act/Assert structure**, visually separated (blank line between
  sections) even in short tests — makes intent scannable at a glance.
- **Unit tests don't touch the database or network** — anything that does
  is an API/integration test, not a unit test, regardless of what test
  runner it's in.
- **Don't over-mock.** If a test needs five mocks to verify one behavior,
  that's usually a sign the unit under test has too many dependencies, not
  a sign you need a mocking framework with more features.
- **Composition-root testing (S-113):** `backend/src/XGArcade.Api/CompositionRoot/*.cs`
  is deliberately integration-tested by default, not unit-tested. These
  files (`AuthSetup.cs`, `CliVerbDispatcher.cs`, `EndpointMapping.cs`,
  `ServiceRegistration.cs`) are almost entirely straight-line DI
  registration, middleware ordering, and endpoint-mapping calls — wiring,
  not logic — and `XGArcade.Api.Tests`'s `WebApplicationFactory` suite
  already exercises that wiring end-to-end on every test run. Writing
  `ServiceRegistrationTests.cs`-style unit tests against `IServiceCollection`
  contents would mostly assert "was `AddScoped<X>` called," which duplicates
  what a failing integration test already tells you, with none of its
  end-to-end confidence.
  The exception is a specific piece of *conditional logic* inside one of
  these files that is (a) a pure function of its inputs and (b) worth
  isolating on its own — most often a security- or correctness-relevant
  branch, not just an `?? default` config read. `AuthSetup.cs`'s
  `IsLocalE2EAuth`/`GetClientIpPartitionKey` are the current example
  (`AuthSetupTests.cs`, marked `internal` + `InternalsVisibleTo` for the
  test project, same as this document's "testability drives structure, not
  the other way around" principle above): real branching with a security
  consequence (ADR-0006's "never guarded only by config alone"), cheap to
  test directly without a host. `CliVerbDispatcher.cs`'s verb-dispatch table,
  `EndpointMapping.cs`'s middleware/endpoint registration, and
  `ServiceRegistration.cs`'s DI wiring have no comparable logic today —
  don't add unit tests for them speculatively. Re-evaluate a given file
  the moment it grows real conditional logic of its own, the same way
  `AuthSetup.cs` did — this is a per-file judgment call, not a blanket
  exemption for the whole folder.

## Comments and documentation in code

- Comments explain **why**, not **what** — the code already says what it
  does; a comment repeating that in English adds noise, not information.
- Reference REQ/ADR/COMP IDs in comments where a piece of code exists
  *because* of a specific decision, especially a non-obvious one (e.g. "//
  never merge with PlayerData — see ADR-0007" at the exact point where
  that boundary could accidentally be crossed).

## Git and PRs

See `CLAUDE.md`'s "Git and PR conventions" section — kept there rather
than duplicated here, since it's about workflow, not code style.
