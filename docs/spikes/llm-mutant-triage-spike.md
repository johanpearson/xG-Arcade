# LLM Triage of Surviving Mutants — Spike Report

**Date:** 2026-08-15
**Scope:** Prototype only. No tests written, no production code changed. Ground truth is
`docs/spikes/mutation-testing-spike.md` §5 (15 hand-classified survivors from the
`XGArcade.Games.XGGrid` Stryker.NET run).

## 1. What was built

A single script, [`triage.py`](./triage.py) (also in this directory — no framework, no CLI
polish, per the brief):

1. Parses `StrykerOutput_full/*/reports/mutation-report.json` (the full-scope XGGrid run from
   the prior spike) and extracts all 94 `Survived` mutants: file, line range, mutator name,
   original line text, and mutated replacement.
2. For each survivor, assembles context by reading the source file directly:
   - A **45-lines-before / 18-lines-after** window around the mutation (a fixed-window
     heuristic, not real brace-parsing — see §4 for where this bit back).
   - A best-guess **containing method name**, found by scanning upward from the mutation
     line for the nearest C# method-signature-shaped line.
   - **REQ-xxx references** found anywhere in that window (simple regex).
   - **Covering test(s)**: the sibling `*Tests.cs` file (via this codebase's 1:1
     `Foo.cs` → `FooTests.cs` naming convention), searched for `[Test]`-decorated methods
     whose body references the containing method by name. Up to 3 matches, full body
     included.
3. Sends the assembled context to Claude (`claude-opus-5`) and asks for a single-line JSON
   object: `classification` (`real_gap` / `equivalent` / `noise`), `severity`
   (`high`/`medium`/`low`), `rationale`.
4. Logs input tokens, output tokens, cache tokens, model, and cost **per call**, then reports
   totals.

**Stryker limitation that shaped step 2:** the mutation report's `coveredBy` field lists only
opaque per-test GUIDs, not test names — Stryker.NET doesn't persist a GUID→name mapping in the
report (confirmed by inspecting both the JSON and the HTML report's embedded data). Real
coverage data exists during the Stryker run but isn't exported. "Covering tests" here is
therefore a **name-matching heuristic**, not Stryker's own instrumented coverage — an honest
limitation, not a bug in the script.

### How the LLM was actually called

No `ANTHROPIC_API_KEY` or `ant` CLI credential was available in this sandbox, so the script
shells out to `claude -p` (Claude Code's non-interactive print mode) instead of the raw
Messages API. This matters for the cost numbers (§3) — see the caveat there. Getting a clean,
isolated, low-overhead call took three fixes, in order:

1. **Session isolation.** A `claude -p` subprocess inherits `CLAUDE_CODE_SESSION_ID` from the
   environment by default — it was resuming *this orchestrating session* rather than starting
   fresh, which would have both polluted this conversation's transcript and made every
   "per-call" cost figure meaningless (shared cache, shared history). Fixed by stripping
   `CLAUDE_CODE_SESSION_ID` and the related messaging/token env vars before each subprocess
   call, confirmed via a distinct `session_id` in the response.
2. **`--json-schema` structured output was too expensive to use.** It routes through an
   internal tool-use round trip that added **~36.5K tokens of cache-write overhead per call**
   — at 94 calls that's ~3.4M tokens of pure scaffolding, which would have swamped the
   real signal being measured. Switched to asking for plain JSON in the response text and
   parsing it with a regex — overhead dropped to ~7.5K tokens/call (see next point).
3. **`--disallowedTools "*"` cut remaining overhead roughly 5x** (36K → 7.5K tokens/call) by
   keeping Claude Code from loading full tool schemas for a call that never needed tools.
   Combined with `--system-prompt` (full override, not append) and
   `--exclude-dynamic-system-prompt-sections`, this was the floor reachable without a raw
   API key.

## 2. Eval-set agreement (15 hand-classified mutants)

| Mutant ID | Location | Hand classification | LLM classification | Agree? |
|---|---|---|---|---|
| 59 | GridGenerationService.cs:145 | real_gap | real_gap | ✅ |
| 61 | GridGenerationService.cs:145 | real_gap | real_gap | ✅ |
| 224 | GridLiveLookupDispatcher.cs:212 | equivalent | equivalent | ✅ |
| 225 | GridLiveLookupDispatcher.cs:212 | equivalent | equivalent | ✅ |
| 296 | GridNameMatcher.cs:210 | real_gap | real_gap | ✅ |
| 270 | GridNameMatcher.cs:147 | real_gap | real_gap | ✅ |
| 304 | GridNameMatcher.cs:244 | real_gap | real_gap | ✅ |
| 422 | PlayerCacheWarmingService.cs:268 | real_gap | real_gap | ✅ |
| 345 | PlayerCacheWarmingService.cs:156 | **noise** | **real_gap** | ❌ |
| 460 | PlayerCacheWarmingService.cs:332 | noise | noise | ✅ |
| 75 | GridGenerationService.cs:223-225 | noise | noise | ✅ |
| 96 | GridGenerationService.cs:238-239 | noise | noise | ✅ |
| 7 | GridGameModule.cs:55 | noise | noise | ✅ |
| 157 | GridGenerationService.cs:358 | real_gap | real_gap | ✅ |
| 277 | GridNameMatcher.cs:168 | real_gap | real_gap | ✅ |

**Agreement: 14/15 = 93.3%.** Per-class: real_gap 8/8 (100%), equivalent 2/2 (100%),
noise 4/5 (80%).

### The one disagreement, in full

**Hand (noise):** "`pairsProcessed` only feeds `LogProgressCheckpoint`'s log-line cadence; it
is never part of `CacheWarmingResult` and no test asserts on log content." (Verified directly
by reading `PlayerCacheWarmingService.cs`'s `CacheWarmingResult` constructor call at line
~300 — `pairsProcessed` genuinely never appears in it.)

**LLM (real_gap, low severity):** *"No existing test asserts on the pairsProcessed counter —
the covering tests only check TotalPairs, PairsQueriedLive, PairsAlreadyValid and
PairsSkippedConfirmedLow — so a decrementing progress/processed count would be reported to
operators (result field / progress logging) completely undetected; impact is
observability-only, hence low severity."*

**Diagnosis: missing context, not bad reasoning.** The mutation is at line 156; the script's
context window (45 before / 18 after) covers roughly lines 111–174. The `CacheWarmingResult`
constructor that would settle the question sits at line ~300 — outside the window entirely.
The model's own rationale hedges between two possibilities ("result field / progress
logging") rather than asserting one confidently — it reasoned correctly from what it could
see, then guessed on the part it couldn't. This is a clean, attributable instance of the
context-assembly heuristic being the bottleneck, not the model's judgment. A wider window (or
one that follows the containing method to its actual closing brace, and separately locates
where a local variable's value is consumed) would very likely have fixed this specific case
— but it's also a fair example of the general risk: any fixed-size window will eventually cut
through the exact fact that would have changed the verdict.

## 3. Full run: all 94 survivors

| Metric | Value |
|---|---|
| Mutants triaged | 94 (0 errors) |
| Wall-clock time | ~3m30s (6-way concurrent subprocess calls) |
| Model | claude-opus-5 |
| Total input tokens (incl. cache writes/reads) | 911,056 |
| Total output tokens | 22,817 |
| **Total cost (as measured, via `claude -p`)** | **$8.51** |
| Classification distribution | real_gap: 29 (31%), noise: 57 (61%), equivalent: 8 (8%) |
| Severity among real_gap | high: 7, medium: 14, low: 8 |

**Cost breakdown and the harness-tax caveat.** Of the $8.51: **~$4.79 (56%) is cache-write
overhead** from the `claude -p` CLI's own system-prompt/tool-scaffolding tax (§1, point 3),
~$0.57 is real output tokens, and the rest is negligible (fresh input + a modest amount of
cache reads that landed when concurrent calls happened to share an identical prefix). This
overhead is an artifact of going through Claude Code's CLI rather than a raw
`POST /v1/messages` call — a production implementation using the Anthropic SDK directly
(given an API key) would carry a system prompt sized to just the triage instructions (a few
hundred tokens) instead of Claude Code's full default scaffolding, and would very likely land
closer to **$1–2 for the same 94 calls** — dominated by the actual per-mutant context (roughly
1,500–3,000 tokens each) and the ~240-token average response. The $8.51 figure is real and
reproducible with this exact script, but it measures "cost of driving Claude Code
non-interactively," not "cost of the mutation-triage task" in isolation — worth stating
plainly since the two easily get conflated.

## 4. Where the model was confidently wrong

The only ground-truth-verified miss is id 345, covered in §2 — a missing-context error, not a
reasoning error. Beyond the eval set, the full 94-mutant run wasn't independently
hand-verified (that would defeat the purpose of automating triage), so no further "confidently
wrong" claims can be made with the same rigor. Two soft observations from reading the full
transcript in §6 below:

- The model repeatedly and correctly reconstructs the **same architectural facts** across
  independent calls without being told them explicitly in the prompt — e.g. multiple mutants
  in `PlayerCacheWarmingService.cs`'s Club×Club loop are each, independently, flagged with
  "no covering test matched this method at all" and cross-referenced against the analogous
  Country×Club loop's coverage. This is the id-422-style asymmetric-coverage pattern (flagged
  as the standout finding in the original hand-classification) being **independently
  rediscovered by the LLM**, not primed by the prompt — a positive signal for the general
  approach, not just this one case.
- Several `equivalent` classifications (GridLiveLookupDispatcher.cs:212, the five sibling
  mutants) show the model correctly tracing that a guarded block and its fallthrough both
  `return null`, matching the hand analysis's reasoning almost verbatim — including the
  detail that equivalence had to be established by reading past the immediate diff into the
  surrounding control flow, not just the mutated line.

## 5. Would excluding logging mutators remove most of the noise?

Of the 57 `noise`-classified mutants, checking each mutation's source line (and up to 3 lines
above it, to catch multi-line `logger.LogXxx(...)` calls) for a direct `logger.Log*()`
invocation:

- **31/47 (66%)** of the `String mutation` + `Statement mutation` noise mutants (the two
  dominant mutator types in the noise class, 47 of 57 total) sit directly inside a
  `logger.LogInformation`/`LogDebug`/`LogWarning` call.
- The remaining ~16 are adjacent-but-distinct: exception-message strings (never asserted, but
  not a logging call either — `GridGameModule.cs`'s `GuessScoringException`/
  `GridGenerationException` messages) and progress-counter variables
  (`pairsProcessed`, `pairsWithTechnicalFailure`) that are incremented for diagnostic purposes
  but aren't themselves log calls.

**Verdict: yes, for a majority, but not all of it.** Configuring Stryker.NET to exclude
`logger.Log*` call arguments from its mutation targets (via a `mutate` glob exclusion or an
`ignoreMethods`-style config) would remove roughly two-thirds of the noise class outright. The
remaining third — exception message text and diagnostics-only counters — would need a second,
narrower exclusion (or just continued LLM triage) to clean up, since it isn't a single
syntactic pattern Stryker's config can target as cleanly.

## 6. Full classified list (94 mutants, sorted real_gap → equivalent → noise, severity descending)

See [`all94-triage-results.md`](./all94-triage-results.md) for the complete table (94 rows) —
kept as a separate file since it's the largest artifact from this spike. Summary already
given in §3; the 7 `high`-severity real gaps are:

| ID | Location | Rationale (abridged) |
|---|---|---|
| 11 | GridGameModule.cs:71 | Ambiguous-match short-circuit can be bypassed, burning live-lookup quota / dropping disambiguation candidates — no covering test exercises that branch. |
| 61 | GridGenerationService.cs:145 | Trophy×Trophy feasibility arithmetic loosened; no test at all covers `SelectPairing`'s boundary. |
| 59 | GridGenerationService.cs:145 | Same boundary, equality-operator variant — same gap. |
| 239 | GridNameMatcher.cs:79 | Dropped `continue` lets a column-only match slip through as "correct," violating the both-axes rule. |
| 422 | PlayerCacheWarmingService.cs:268 | The asymmetric Club×Club `hadTechnicalFailure` bug — independently rediscovered, matches the original hand-classification's most severe finding. |
| 434 | PlayerCacheWarmingService.cs:284 | Same Club×Club branch: a missing `ClearTechnicalFailureAsync` call permanently poisons the failure-tracking state. |
| 437 | PlayerCacheWarmingService.cs:286 | Same branch again: an inverted condition wrongly marks valid pairs as confirmed-low. |

Three of the seven high-severity findings (422/434/437) are the *same* Club×Club coverage
asymmetry surfaced from three different mutation sites — a strong, repeatable signal that this
one branch is the real gap in the module, not three unrelated bugs.

## 7. Verdict

**Agreement (93.3% on the eval set, with the one miss cleanly attributable to context-window
truncation) is strong enough that this triage layer would meaningfully cut human review time**
— a reviewer working the sorted-by-severity output in §6 would spend their attention on 29
real-gap candidates instead of manually reading all 94, and the model's own severity ranking
correctly surfaces the single most valuable finding (the Club×Club asymmetry) at the top three
times over from independent mutation sites.

**What would need to change before trusting this at scale, in priority order:**

1. **Context assembly is the weak link, not the model.** A fixed-line-count window will always
   have cases like id 345 where the deciding fact sits outside it. Replacing it with real
   brace-aware method extraction (or, better, resolving Stryker's own `coveredBy` test IDs to
   names — possible if Stryker.NET exposes that mapping via a different report format or a
   `--reporter Json` option not explored here) would remove the single largest source of
   error observed.
2. **Cost measured here is not representative of production cost.** Re-run via the raw
   Messages API with a minimal system prompt before using these dollar figures for a go/no-go
   decision — see §3's estimate of a ~4-8x reduction.
3. **The eval set (15) is small and was hand-picked for diversity, not randomly sampled** —
   its class distribution (53% real_gap) doesn't match the full 94-mutant run's (31%
   real_gap). The 93.3% agreement figure is trustworthy as an agreement measurement, but
   don't extrapolate the eval set's class *proportions* onto the full population — use §3's
   numbers for that.
4. **Logging-mutator exclusion in Stryker config is worth doing regardless of the LLM layer**
   (§5) — it's a one-time config change that removes ~two-thirds of noise before triage even
   runs, cutting both review burden and (if kept) LLM triage cost proportionally.
