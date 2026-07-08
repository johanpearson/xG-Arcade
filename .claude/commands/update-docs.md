---
description: Sync requirements/architecture/implementation docs and CHANGELOG against the current diff
---

Run the "after finishing work" documentation workflow defined in `CLAUDE.md`.

Use the `doc-sync` subagent to:

1. Diff the current working state against the base branch (or the last
   commit if no branch comparison is applicable).
2. Check each of `docs/requirements-document.md`, `docs/architecture-document.md`,
   and `docs/implementation-document.md` against their own `update_when`
   frontmatter triggers.
3. Update whichever docs are out of sync, bump their `last_updated`/`version`
   frontmatter, and append a `docs/CHANGELOG.md` entry.
4. If a change looks architecturally significant, scaffold a new ADR with
   `/new-adr` instead of just editing prose, and say so explicitly.

Finish with a short summary: docs updated, docs checked-and-unchanged, and
any open questions that need a human decision before this can be considered
complete.
