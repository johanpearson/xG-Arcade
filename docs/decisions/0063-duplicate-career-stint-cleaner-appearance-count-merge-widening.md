# ADR-0063: Widen DuplicateCareerStintCleaner's provable-duplicate matching to a null-tolerant AppearanceCount rule, with in-place survivor mutation

- **Status:** Accepted
- **Date:** 2026-08-10
- **Related requirements:** REQ-1203
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)
- **Amends:** ADR-0059 (Canonicalize PlayerCareerStint.ClubName by Wikidata QID, not by label)

## Context

ADR-0059 gave `DuplicateCareerStintCleaner` a deliberately narrow,
"provable-only" matching rule: a non-canonical `PlayerCareerStint` row is
removed only when another row exists for the *exact same*
`(PlayerId, StartYear, EndYear, AppearanceCount)` tuple whose `ClubName`
**is** a seeded `ClubDefinition.Name`. That ADR's own "For AI agents"
section states this matching "must not be widened into a fuzzy/alias match
without a fresh ADR — that would repeat exactly the correctness risk
`NormalizeClubName`'s own doc comment already rejected once."

A 2026-08-10 bug report (screenshots) showed the exact-tuple rule was
itself too narrow to catch a real, common duplicate shape: two rows for
the same real stint where one writer recorded `AppearanceCount` and the
other didn't (`AppearanceCount IS NULL`) — e.g. "AC Milan 25 apps" /
"AC Milan 95 apps," "Real Sociedad 2 apps" / bare "Real Sociedad." A null
`AppearanceCount` means "unknown," not "zero," and not "a different
number than the other row" — so the exact-tuple rule's `AppearanceCount`
component was rejecting matches that are, in fact, provably the same real
stint.

The same day, `WikidataClient.ParseCareerStintBindings`'s go-forward
(live-parse) dedup was given an equivalent fix
(`MergeCareerStintEntries`): entries sharing `(ClubName, StartYear,
EndYear)` merge into one when at most one distinct populated
`AppearanceCount` value exists among them, keeping that value. Leaving the
retroactive cleanup on the old strictly-exact rule while the go-forward
path used the new null-tolerant rule would mean the two paths permanently
diverge on what counts as "the same stint" — new duplicates newly written
would self-heal, but the ~608K-row table's *already-persisted* duplicates
of this exact shape would never be cleaned, defeating half the point of
having a retroactive cleaner at all.

Separately, the original cleanup only ever compared a seeded `ClubName`
against a *different*, non-seeded label (Step 1). It never compared two
rows that already share the exact same `ClubName` — which is exactly the
shape of the "AC Milan 25 apps" / "AC Milan 95 apps" report, since both
rows in that pair use the identical, already-canonical label "AC Milan."

## Decision

Widen `DuplicateCareerStintCleaner`'s matching rule in two ways, applying
the identical null-tolerant `AppearanceCount` rule `WikidataClient
.MergeCareerStintEntries` already established for the go-forward path, so
both the retroactive cleanup and the live-parse path converge on the same
definition of "provably the same stint":

1. **Null-tolerant `AppearanceCount` matching (Step 1, cross-writer
   name-variant duplicates).** A null `AppearanceCount` on one side and a
   populated value on the other now counts as a match — the null side is
   informationally subsumed, never treated as a conflict. When this
   happens, the surviving canonical row's `AppearanceCount` is mutated
   **in place** to the populated value before the non-canonical row is
   deleted, so the more informative value isn't silently discarded along
   with the row that carried it. This is the one deliberate exception to
   this class's previous "only ever deletes, never writes to a surviving
   row" behavior.
2. **A second pass (Step 2, same-`ClubName` duplicates).** Groups rows by
   `(PlayerId, ClubName, StartYear, EndYear)` — a match criterion this
   class never had before, since Step 1 only ever compared a seeded name
   against a *different* non-seeded label. Applies the identical
   null-tolerant rule within each group.

Both widenings preserve, unchanged, the one non-negotiable carve-out
ADR-0059's own limitation note already established and this ADR does not
touch: **two rows with DIFFERENT, both-populated `AppearanceCount` values
are never merged**, seeded-name match or not, same-`ClubName` or not —
they could plausibly be two genuinely different stints (e.g. a
loan-and-return spell recorded as two separate statements), and treating
that as provable would be exactly the correctness risk `NormalizeClubName`
and ADR-0059 already rejected once. This carve-out is generalized (not
loosened) to 3+-row groups too: a group of more than two rows sharing a
key is only auto-merged when at most one distinct populated
`AppearanceCount` exists across the *whole* group; if more than one
distinct populated value exists anywhere in the group, the *entire* group
is left untouched rather than mutating a canonical row's value based on
enumeration order (a real risk once merging is done via in-place mutation
across more than two rows — see Consequences).

The scope this widening does **not** touch: still no `ClubName` fuzzy/
alias matching of any kind (a seeded name is still only ever matched
against a non-seeded label via the unchanged Step 1 grouping key, or
against an identical label via the unchanged Step 2 grouping key — never
a "close enough" string comparison). Still no live Wikidata re-query, no
QID-based matching (no QID exists on an already-persisted row to match
on, same limitation ADR-0059 already documented). Still a manually
triggered, idempotent, one-off CLI verb, not wired into
`migrate-and-seed`.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Widen `AppearanceCount` matching only in `WikidataClient` (go-forward path), leave the retroactive cleaner on the strict exact-tuple rule (chosen: rejected) | No change to a class ADR-0059 explicitly locked down; smallest diff | The two paths permanently diverge on "same stint" definition; the reported bug's already-persisted duplicates (this is a retroactive-cleanup bug report, not a go-forward one) are never actually fixed by this option | Rejected — defeats the purpose of having a retroactive cleaner for exactly this shape of duplicate |
| Keep Step 1/Step 2 delete-only (never mutate a surviving row); on a null-vs-populated match, delete the null row and keep the canonical row's null `AppearanceCount` as-is | Preserves "only ever deletes" invariant exactly | Silently drops the more informative populated value the deleted row carried — the surviving row ends up with LESS information than existed pre-cleanup, which is worse than doing nothing | Rejected — loses real data for no benefit |
| Full purge-and-reseed of `PlayerCareerStint` instead of another narrow patch | Guaranteed-correct end state; no more incremental patching | Same disproportionate availability-regression cost ADR-0059's own Alternatives table already rejected this option for; nothing about this widening changes that cost/benefit calculus | Rejected — ADR-0059's reasoning for rejecting this still applies unchanged |
| Widen Step 1 only (null-tolerant), skip the new same-`ClubName` Step 2 pass | Smaller diff; fixes the null-vs-populated shape | Does not fix the "two rows already share the identical seeded `ClubName`" shape from the bug report (e.g. "AC Milan 25"/"AC Milan 95," "Real Sociedad 2"/bare "Real Sociedad") — a real, reported duplicate shape left uncleaned | Rejected — leaves half the reported bug unfixed |
| Fuzzy/alias `ClubName` matching (e.g. edit-distance or a label-alias table) | Could theoretically catch more duplicate shapes in one pass | Exactly the correctness risk `NormalizeClubName`'s own doc comment and ADR-0059's Alternatives table already rejected once — risks merging two genuinely different clubs that happen to share a name fragment | Rejected — this ADR explicitly does NOT do this; matching stays limited to exact-`ClubName`-or-seeded-vs-non-seeded-pair, with only the `AppearanceCount` component made null-tolerant |

## Consequences

- Positive: the retroactive cleanup and `WikidataClient
  .MergeCareerStintEntries`'s go-forward parse now apply the identical
  "same real stint" definition, so the reported duplicate shapes
  (null-vs-populated `AppearanceCount`, same-`ClubName` pairs) are cleaned
  up wherever they already exist in the ~608K-row table, not just
  prevented from recurring going forward.
- Positive: no information loss on merge — a populated `AppearanceCount`
  observed on a row being removed is preserved onto the surviving row
  rather than silently discarded.
- Negative / trade-off accepted: this class is no longer strictly
  delete-only. A surviving row's `AppearanceCount` can now be mutated in
  place. This is a genuine behavior change from ADR-0059's original
  design and is the reason this ADR exists rather than treating the fix
  as a routine bug patch — mutating a "canonical" row's data, even
  additively (null → populated, never populated → different-populated),
  is qualitatively different from ADR-0059's original "only ever prove
  and delete" contract.
- Negative / trade-off accepted: for a group of 3+ rows sharing a key
  where the group as a whole has more than one distinct populated
  `AppearanceCount`, the entire group is left untouched, even the pairs
  within it that might otherwise look mergeable in isolation. This is
  deliberately conservative — the alternative (merging some rows in a 3+
  group while leaving others) reintroduces exactly the order-dependent,
  unpredictable-outcome risk this fix exists to eliminate, so ambiguity at
  the group level short-circuits the whole group rather than being
  resolved row-by-row.
- Follow-up: none currently planned. If a further duplicate-node report
  surfaces a shape this widened rule still misses (e.g. a genuinely
  different stint that happens to look mergeable under this rule, or a
  duplicate shape neither Step 1 nor Step 2's grouping key catches), that
  needs its own fresh ADR per ADR-0059's guardrail, now pointed at this
  one — see "For AI agents" below.

## For AI agents

This ADR is itself the "fresh ADR" ADR-0059's "For AI agents" section
required before widening `DuplicateCareerStintCleaner`'s matching. Any
**further** widening beyond what's described above — in particular any
move toward `ClubName` fuzzy/alias matching, or toward treating two
DIFFERENT, both-populated `AppearanceCount` values as a match — needs its
own new ADR referencing this one, not a silent code change. The
`AppearanceCount` null-tolerant rule and the same-`ClubName` grouping key
described here are the full extent of what's authorized; do not extend
either Step's grouping key (e.g. to a fuzzy date match, or to dropping the
`PlayerId` component) without a fresh ADR of its own.
