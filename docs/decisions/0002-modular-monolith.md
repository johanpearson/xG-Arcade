# ADR-0002: Modular monolith instead of microservices

- **Status:** Accepted
- **Date:** 2026-07-04
- **Related requirements:** REQ-601, REQ-602
- **Related components:** COMP-01 through COMP-07

## Context

The platform is intended to eventually host multiple games sharing a common
core (users, leagues, rounds, scoring). A microservices approach would give
strong isolation between game modules, but the project is built and
maintained by a single developer as a side project with a strict free-tier
cost constraint.

## Decision

The backend is built as a single deployable ASP.NET Core application,
internally divided into modules with explicit boundaries (`Core`, game
modules such as `Games.Grid`, `Data`, `DataSync`). Game-specific logic is
isolated behind an `IGameModule` interface rather than a network boundary.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Microservices per game | Strong isolation, independent deployability | Multiple services to host (cost), operational overhead (service discovery, inter-service auth, distributed tracing) disproportionate to team size | Violates REQ-602 (cost) and adds complexity with no current benefit |
| Single undifferentiated codebase (no module boundaries) | Simplest to start | Game-specific logic would leak into shared code, making a second game costly to add and hard to test in isolation | Undermines the platform vision and REQ-601 testability |
| Modular monolith (chosen) | One deployable unit (low cost), clear boundaries enable independent testing and a future extraction path if ever needed | Requires discipline to keep module boundaries clean without compiler-enforced service isolation | Best fit for current scale and constraints |

## Consequences

- Positive: single, low-cost deployment; module boundaries still give most
  of the testability and separation-of-concerns benefits of microservices
- Negative / trade-offs accepted: boundary discipline is enforced by code
  review / architecture review rather than infrastructure; a runaway
  dependency from a game module into another game module's internals is
  possible if not caught
- Follow-up: if a second game module is added, use it as a test of whether
  the `IGameModule` boundary actually holds in practice before considering
  any further structural change

## For AI agents

When adding a new game module, implement `IGameModule` and place it under
`xG Arcade.Games.<Name>`. Do not have one game module reference another
game module's namespace directly, and do not bypass COMP-06 to access
player data (see ADR-0001). If you find yourself needing to break this
boundary, stop and flag it rather than adding a workaround.
