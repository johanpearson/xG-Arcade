---
doc_id: coding-guidelines
title: Coding Guidelines
version: "0.4"
status: draft
last_updated: 2026-07-17
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
