# xG Arcade

A multi-game platform built around football/soccer trivia and guessing
games. The first game is **xG Grid** — an NxN grid where players combine
two categories (club × club, club × country) to guess a matching player,
scored on how unique their answer is compared to other players.

"xG Arcade" is a working name — see `docs/decisions/0003-generic-round-game-reference.md`
for why renaming it again later is a clean find-and-replace, not a redesign.

## Start here

| If you want to... | Go to |
|---|---|
| **Know what to actually build right now** | `MVP-SCOPE.md` — read this first |
| Pick the next development story to implement | `docs/backlog.md` (S-001 → S-013, in order) |
| Understand what to build and why | `docs/requirements-document.md`, `docs/architecture-document.md` |
| See how it should look/feel | `docs/design-document.md`, `mockups/design-mockups.html` |
| Actually start building with Claude Code | `CLAUDE.md`, then `.claude/README.md` |
| Set up external accounts (MVP needs only GitHub/Azure/Supabase) | `SETUP.md` |
| Deploy or manage infrastructure | `infra/README.md` |
| See what's left to do | `TODO.md` |
| Check code style/testing conventions | `docs/coding-guidelines.md` |
| See informal gotchas/context from development | `NOTES.md` |
| Understand a past decision | `docs/decisions/` (ADRs, numbered) |
| Check what changed recently | `docs/CHANGELOG.md` |

## Stack

C# / .NET 10 (LTS) backend, TypeScript / React 19 frontend, PostgreSQL via
Supabase (also handling auth), hosted on Azure Container Apps + Static Web
Apps, all within free tiers at current scale. Full rationale in
`docs/implementation-document.md` and the ADRs it references.

`docs/implementation-document.md` §1 is the source of truth for exact
versions if this section and that doc ever disagree — this README doesn't
get updated automatically when Dependabot bumps something.

## Status

Documentation and architecture are complete through several rounds of
review (see `docs/CHANGELOG.md` for the full history). No application code
exists yet — `TODO.md` has the actual starting checklist.
