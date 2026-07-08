---
description: Scaffold a new Architecture Decision Record from the template
---

Create a new ADR for a decision made during this session.

1. Look at `docs/decisions/` and find the highest existing ADR number; the
   new file is `docs/decisions/NNNN-short-title.md` with NNNN incremented
   and zero-padded to 4 digits.
2. Copy the structure from `docs/decisions/0000-template.md`.
3. Fill in Context, Decision, Alternatives considered, and Consequences
   based on what was actually discussed/decided in this session — ask for
   any missing detail (e.g. alternatives that were considered but not
   written down) rather than inventing plausible-sounding ones.
4. Set `Status: Accepted` only if the decision is actually final; use
   `Proposed` if it still needs confirmation.
5. Fill in "Related requirements" and "Related components" by cross-checking
   `docs/requirements-document.md` and `docs/architecture-document.md` —
   don't leave these blank.
6. Add a row for the new ADR to the table in `docs/architecture-document.md`
   §10.
7. Append a `docs/CHANGELOG.md` entry noting the new ADR.

If this decision supersedes an earlier ADR, update the old ADR's `Status`
line to `Superseded by ADR-NNNN` — do not rewrite its content.
