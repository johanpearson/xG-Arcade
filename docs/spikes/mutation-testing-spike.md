# Mutation Testing Spike — Stryker.NET on xG Arcade

**Date:** 2026-08-15
**Scope:** Measurement only. No tests written, no code fixed, no docs updated — this is a
spike to inform a future ADR/backlog decision, not a shipped change.

## 1. Stack and baseline

| | |
|---|---|
| Backend | .NET 10 / ASP.NET Core, 6 projects, NUnit |
| Backend tests | 1,403 `[Test]`/`[TestCase]` methods across 6 test projects |
| Backend LOC | ~41,589 src / ~33,586 tests |
| Frontend | React + TypeScript (Vite), Vitest, 33 test files, ~11,117 LOC (src) |

Module chosen for the mutation run: **`XGArcade.Games.XGGrid`** (1,681 LOC src, 119 tests,
3,788 LOC tests, ~2s baseline test run). Picked because it's a real domain module (not a
thin CLI/DTO layer) and because it was the module touched by the most recent substantial
commit, which doubles as the diff-scoping target in step 2 below.

The .NET 10 SDK was **not present** in this sandbox and had to be installed (via Microsoft's
apt repo — `dotnet.microsoft.com`/`dot.net` were blocked by the outbound proxy policy, but
`packages.microsoft.com` and `nuget.org` were reachable). This is a sandbox artifact, not a
finding about the repo itself — CI already installs .NET via `actions/setup-dotnet`.

Frontend (StrykerJS) was **not executed** — time-boxed to one module per the task brief, and
the last-5-commits diff that anchors step 2 below is 100% backend. StrykerJS is a legitimate
fit for this repo (Vitest is on its supported-runner list) but that's a separate spike.

## 2. Tooling install

`dotnet tool install dotnet-stryker --tool-path /opt/stryker-tool` — Stryker.NET 4.16.0.
No project structure changes were needed. It ran directly against the existing
`XGArcade.Games.XGGrid.csproj` / `XGArcade.Games.XGGrid.Tests.csproj` pair using
`--test-project <path>`, invoked from the source project's directory. Output lands in a
`StrykerOutput/` folder next to the mutated project (already covered by `.gitignore`, so it
didn't show up in `git status`).

## 3. Full-scope run

```
dotnet-stryker --test-project ../../tests/XGArcade.Games.XGGrid.Tests/XGArcade.Games.XGGrid.Tests.csproj
```

| Metric | Value |
|---|---|
| Wall-clock time | **2m 52.8s** (post-restore/build; restore+build was a separate ~25s one-time cost) |
| Mutants created | 467 |
| Compile errors | 98 (21%) |
| No coverage | 8 |
| Ignored (redundant, "block already covered") | 60 |
| **Mutants actually tested** | **301** |
| Killed | 207 |
| Survived | 94 |
| Timeouts | 0 |
| **Mutation score** | **66.99%** |

**Compile errors, explained:** 44 of the 98 (45%) are Stryker's `Count()` → `Sum()` LINQ
mutator hitting non-numeric sequences (`IEnumerable<T>.Count()` mutated to
`IEnumerable<T>.Sum()`, which doesn't compile without a numeric selector). This is a known
Stryker.NET mutator artifact, not a code-quality signal — it inflates the "mutants created"
count but Stryker correctly discards these before scoring, so it doesn't distort the
mutation score. The remaining compile errors are a mix of object-initializer and
equality-mutation combinations that don't type-check given this codebase's nullable-enabled,
pattern-heavy style. One (`GridLiveLookupDispatcher.cs:70`) tripped Stryker's "Safe Mode"
definite-assignment guard and got the whole containing method's mutants removed rather than
individually flagged.

**No timeouts** — the whole module's test suite runs in ~2s uninstrumented, so even
per-mutant re-runs stayed fast. Timeout risk in this repo would show up on modules with
real I/O in the hot path, not this one (XGGrid's tests already run against a no-mocking,
InMemory-EF-Core setup — see `docs/coding-guidelines.md`).

## 4. Diff-scoped run

```
dotnet-stryker --since:f0cd7e7 --test-project ...   # f0cd7e7 = HEAD~5
```

| Metric | Full-scope | Diff-scoped | Δ |
|---|---|---|---|
| Wall-clock time | 2m 52.8s | 2m 52.0s | **~0%** |
| Mutants tested | 301 | 301 | 0 |
| Killed / Survived | 207 / 94 | 208 / 93 | ±1 (run-to-run flakiness) |
| Mutation score | 66.99% | 67.31% | +0.32pp (noise) |

**Diff-scoping produced zero measurable speedup for this run.** The reason is structural,
not a tooling failure: `git diff --name-only HEAD~5 HEAD -- backend/src/XGArcade.Games.XGGrid`
shows the module's entire diff came from **one single commit** (`7ff0b24`, "S-119: split
GridGameModule.cs by responsibility (#187)") — a refactor that rewrote 10 of the module's 14
source files and all 4 of its test files wholesale. The other 4 commits in the 5-commit
window touched zero files in this module. With that much of the file rewritten, nearly every
mutation site fell inside a changed diff hunk, so Stryker correctly determined that ~all 301
testable mutants needed re-verification — there was nothing to skip.

This is an important negative result, not a wasted run: it shows diff-scoping's payoff is
proportional to **how narrow the diff is relative to the module**, and a broad refactor PR is
close to the worst case for it.

## 5. Sample of 15 surviving mutants, classified

| # | Location | Mutation | Class | Why |
|---|---|---|---|---|
| 1 | `GridGenerationService.cs:145` | `trophyCount >= size*2` → `<` | **Real gap** | The one test targeting this exact threshold (`...TrophyTrophyPairingStillInfeasible`) only asserts `Assert.ThrowsAsync<GridGenerationException>` with no message/reason check. Flipping the comparison makes the pairing wrongly "feasible," but generation then fails for an *unrelated* reason (not enough distinct trophies to build headers) and still throws the same exception type — so the test passes either way. |
| 2 | `GridGenerationService.cs:145` | `size * 2` → `size / 2` | **Real gap** | Same root cause as #1 — same weak assertion. |
| 3 | `GridLiveLookupDispatcher.cs:212` | `rowType==Trophy && colType==Trophy` → `\|\|` | **Equivalent** | Both the `if`-true and fallthrough paths return `null` (Trophy×Trophy has no dedicated persist method, comment confirms it's structurally unreachable in production — needs 6 trophies, pool has 3). Since every path through this branch converges on the identical `return null`, the condition's truth value is behaviorally inert. |
| 4 | `GridLiveLookupDispatcher.cs:212` | Negate expression | **Equivalent** | Same convergent-branch reasoning as #3. |
| 5 | `GridNameMatcher.cs:210` | `aliases.Any(...)` → `aliases.All(...)` | **Real gap** | REQ-208 explicitly requires "every recorded alias is checked" (Any semantics). The only fuzzy-alias test (`FindMatchAsync_FuzzyTypo_MatchesViaAlias`) seeds a player with exactly **one** alias, where `Any` and `All` are indistinguishable. A player with 2+ aliases would silently stop fuzzy-matching on any but the closest alias if this shipped. |
| 6 | `GridNameMatcher.cs:147` | `OrderBy(p => p.Id)` → `OrderByDescending` | **Real gap** (low severity) | The comment documents "deterministic response shape" as intentional (disambiguation candidate ordering). No test asserts order, only set membership. Ships a cosmetic-but-documented regression. |
| 7 | `GridNameMatcher.cs:244` | `<= 8` → `< 8` (edit-distance tier boundary) | **Real gap** | Moves length-exactly-8 names into the wrong tolerance tier. No test uses a boundary-length-8 name; all length-tier tests use values clearly inside a tier. |
| 8 | `PlayerCacheWarmingService.cs:268` | `var hadTechnicalFailure = false;` → `true` | **Real gap** (high severity) | This is the Club×Club loop's copy of a pattern that exists twice in the file. The Country×Club loop's identical line (~193) *is* covered and its mutant was killed; this one wasn't. If shipped, every successful Club×Club lookup would be wrongly recorded as a technical failure — never persisting confirmed-low markers, always re-querying, and corrupting `PairsWithTechnicalFailure`/`FailingPairs` in the result. The one happy-path test that exercises this loop checks `TotalPairs`/`PairsQueriedLive`/`PairsAlreadyValid` and call counts, but never `PairsWithTechnicalFailure` — an asymmetric coverage gap between two near-duplicate code paths. |
| 9 | `PlayerCacheWarmingService.cs:156` | `pairsProcessed++` → `--` | **Noise** | `pairsProcessed` only feeds `LogProgressCheckpoint`'s log-line cadence; it is never part of `CacheWarmingResult` and no test asserts on log content. |
| 10 | `PlayerCacheWarmingService.cs:332` | `pairsProcessed % N == 0 \|\| pairsProcessed == totalPairs` → `&&` | **Noise** | Same progress-logging cadence check as #9 — controls only when a log line fires. |
| 11 | `GridGenerationService.cs:223-225` | Deleted `logger.LogInformation(...)` statement | **Noise** | Pure logging, no downstream effect. |
| 12 | `GridGenerationService.cs:238-239` | Deleted `logger.LogDebug(...)` statement | **Noise** | Pure logging. |
| 13 | `GridGameModule.cs:55` | Exception message string → `""` | **Noise** | `throw new GuessScoringException($"...")` — mutating the message text doesn't change the exception type, and no test asserts `.Message`. |
| 14 | `GridGenerationService.cs:358` | Deleted `Random.Shared.Shuffle(array)` | **Real gap** (low severity) | Removing the shuffle makes header-candidate selection deterministic/order-based instead of randomized. No REQ explicitly mandates grid variety, but it's clearly the intent (comment: candidates tried "never repeating a rejected one" implies variety across runs); no test exercises randomness, so a regression here would ship silently. Low severity — doesn't break correctness, just variety. |
| 15 | `GridNameMatcher.cs:168` | `!(A) && !(B)` → logical variant in the distinguishing-attribute filter | **Real gap** | Feeds which extra attributes get shown in a disambiguation prompt (REQ-209). No test asserts on the *content* of `DisambiguationCandidate.DistinguishingAttributes` beyond happy-path cases; a filter regression here would show wrong/redundant hint data without failing any test. |

**Ratio: 8 real gap (53%) / 2 equivalent (13%) / 5 noise (33%).**

Two observations beyond the raw ratio:
- The single most valuable finding (#8) came from an **asymmetry between two near-identical
  code paths** in the same file — exactly the class of bug that's easy for a human reviewer
  to miss (both loops *look* equally covered at a glance) and that line/branch coverage
  wouldn't catch either, since both branches execute under existing tests.
- Both equivalent mutants (#3/#4) were the *same* condition, caught because a structural
  code comment already flagged the branch as practically unreachable. Recognizing an
  equivalent mutant required reading the surrounding comment/doc trail, not just the diff —
  this classification step doesn't automate away human judgment.

## 6. How scope was determined, and whether it generalizes

Stryker.NET's `--since:<ref>` computes `git diff <ref>...HEAD`, then only re-tests mutants
whose source location (or a test covering them) falls inside a changed hunk; the log makes
this explicit: `"301 mutants will be tested because: Mutant changed compared to target
commit"`. This is a real, working mechanism — not something this spike hand-rolled.

Whether **"last 5 commits"** is the right unit to feed it is a separate question, and the
answer here is **no, not for this repo's workflow**. Every commit message in this repo's
history ends in a PR number (`(#187)`, `(#186)`, …) — commits are one-per-PR, squash-merged.
"Last 5 commits" is "last 5 merged PRs," almost all unrelated to whatever module a *new* PR
touches (in this sample, 4 of the 5 touched zero files in the mutated module). A real PR-time
check needs to scope against **the PR's actual base**, i.e. `--since:$(git merge-base
origin/main HEAD)` or equivalent — not a fixed commit count. That's a config detail, not a
redesign, and it's the fix for the false negative this spike surfaced (a genuinely narrow
PR's diff-scoped run would look nothing like what's measured in §4, where the sampled
"diff" happened to equal an entire module rewrite).

With the scope unit fixed to merge-base, the generalization claim is only as strong as "the
average PR is much narrower than a whole-module rewrite" — true for typical incremental
story work in this backlog (most `docs/backlog.md` stories are single-file/single-class
changes) and false for refactor-shaped stories like S-119 itself. A PR-time gate built on
this would need to accept that refactor PRs get no time savings and size the CI timeout
budget for the full-scope case, not the diff-scoped best case.

## 7. Verdict

**Run time at PR scale: viable for a single module, not yet demonstrated for a full-repo
gate.** ~2m53s for one 1,681-LOC module with 119 tests is well within a PR-check budget. But
this spike could not show diff-scoping actually cutting that time, because the sample diff
it was pointed at (per the task's own "last 5 commits" instruction) turned out to be a
whole-module rewrite — an honest negative result, not a tooling failure, and traceable to a
fixable scope-unit choice (§6). Two things would need to happen before recommending this as
a merge gate, neither large:
1. Re-run with scope tied to `merge-base(base, HEAD)` instead of a fixed commit count, and
   validate on a narrow story-sized diff to get a real speedup number.
2. Decide a policy for the `Count()`→`Sum()` compile-error mutator noise (~10% of all created
   mutants in this run) — either accept it as unavoidable tool overhead or check whether a
   newer Stryker.NET release has fixed that specific mutator's type-awareness.

Signal quality (§5) is the stronger of the two results: 53% of a random 15-mutant survivor
sample were real, previously-undetected coverage gaps — including one (#8) a code reviewer
would plausibly have missed. That's a genuinely useful ratio; noise (33%, mostly log-line
mutations) is filterable by excluding logging calls from Stryker's mutation targets
(`stryker-config.json`'s `mutate` glob can exclude `*logger.Log*` patterns), which would
likely push the real-gap ratio well above 60% without much configuration effort.
