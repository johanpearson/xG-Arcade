using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGPath;

// COMP-11: IGameModule implementation for xG Path, the second game hosted
// on the platform. S-080 scaffolded the module boundary only; S-081
// implemented REQ-1201 (target-player eligibility) and REQ-1202 (round
// structure — a small, fixed set of distinct-target puzzles). S-082
// implemented REQ-1204 (guess correctness resolution) and REQ-1205
// (per-puzzle attempt cap, fixed at 7), mirroring GridGameModule's
// (COMP-05) established "assemble instance, persist via repository, return
// GameInstance" shape for generation, and its
// GuessSubmission-cast/name-resolution shape for scoring.
//
// S-154 (pure refactor, no behavior change, docs/backlog.md Epic 17): this
// class is now a thin IGameModule adapter — REQ-1201's whole target-player
// eligibility pipeline (candidate narrowing, stint sanitization, the three
// structural checks, the BirthYear/Position floors, ADR-0056's familiarity
// filter) moved to IPathEligibilityService/PathEligibilityService, following
// the same convention docs/decisions/0068-grid-game-module-responsibility-split.md
// established: no facade, independently registered. See that interface's
// own doc comment for the full eligibility history this class used to carry
// (ADR-0047/ADR-0056/ADR-0073/ADR-0074/ADR-0075/ADR-0079/ADR-0081); see this
// class's own doc comment on each remaining method for why it stayed here.
public class XGPathGameModule(
    IPathInstanceRepository pathInstanceRepository,
    IPlayerRepository playerRepository,
    IPlayerAliasRepository playerAliasRepository,
    IPathEligibilityService pathEligibilityService,
    IPlayerCareerStintRefreshService careerStintRefreshService,
    Random? random = null,
    TimeProvider? timeProvider = null) : IGameModule
{
    public const string XGPathGameKey = "xg-path";

    // REQ-1205/ADR-0041: xG Path's fixed per-puzzle attempt cap — matches
    // REQ-1203's fixed 7-turn clue sequence 1:1, regardless of the target
    // player's own stint count N. Mirrors GridGameModule.MaxAttemptsPerCell's
    // naming precedent for the equivalent xG Grid constant.
    private const int MaxAttemptsPerPuzzle = 7;

    // REQ-1202: N distinct targets, no repeats, picked uniformly at random
    // — mirrors GridGameModule's optional constructor Random? param
    // precedent so tests can pin selection deterministically without DI
    // needing to register a Random.
    private readonly Random _random = random ?? Random.Shared;

    // REQ-1208/ADR-0058: LastCycleCompletedAt's write on rollover — same
    // injectable-clock precedent as GridGameModule's own _timeProvider
    // field (falls back to the real clock in production, already
    // registered as TimeProvider.System in Program.cs's DI container).
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string GameKey => XGPathGameKey;

    // ADR-0102: never returns null — xg-path has no real-world-content
    // concept to check against config.LatestGameInstanceId, so it always
    // generates a fresh instance, same as before that interface change.
    public async Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await pathInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new PathGenerationException($"PathTemplate '{config.TemplateId}' not found.");

        var eligiblePlayerIds = await pathEligibilityService.GetEligiblePlayerIdsAsync(cancellationToken);

        // REQ-1202: exactly N puzzles, never fewer — an insufficient pool
        // is a hard abort, not a silently-smaller instance. Unaffected by
        // REQ-1208's cycle tracking below: this checks the total live
        // eligible pool, not the cycle-narrowed "not yet used" subset.
        if (eligiblePlayerIds.Count < template.PuzzleCount)
        {
            throw new PathGenerationException(
                $"Not enough eligible target players to build a {template.PuzzleCount}-puzzle xG Path instance " +
                $"({eligiblePlayerIds.Count} eligible players available).");
        }

        // REQ-1208/ADR-0058: restrict selection to eligible players not yet
        // used as a target in the current cycle, rolling the cycle over
        // first if the live pool's remaining-unused count can't satisfy
        // this generation. GetOrCreateCycleStateAsync creates the very
        // first cycle row (CycleNumber 1) the first time this is ever
        // called, mirroring ILeagueRepository.GetOrCreateGlobalLeagueAsync's
        // idempotent-singleton shape.
        var cycleState = await pathInstanceRepository.GetOrCreateCycleStateAsync(cancellationToken);
        var usedPlayerIds = await pathInstanceRepository.GetUsedPlayerIdsInCycleAsync(cycleState.CycleNumber, cancellationToken);

        // A player recorded as used in an earlier generation who has since
        // dropped out of the live eligible pool (e.g. no longer meets
        // ADR-0056's familiarity threshold) is simply absent from
        // eligiblePlayerIds already — this Where-based subtraction quietly
        // drops their now-irrelevant "used" record rather than erroring,
        // exactly REQ-1208's "their earlier used-this-cycle record is
        // inert" requirement. No special-casing needed.
        var usedPlayerIdSet = usedPlayerIds.ToHashSet();
        var availableThisCycle = eligiblePlayerIds.Where(id => !usedPlayerIdSet.Contains(id)).ToList();

        if (availableThisCycle.Count < template.PuzzleCount)
        {
            // ADR-0058's tolerant rollover rule: the remaining-unused
            // portion of the current live pool dropped below what this
            // generation needs (not necessarily to exactly zero). A new
            // cycle begins — every eligible player, including one used
            // moments ago in the just-completed cycle, becomes selectable
            // again — and this generation's targets are selected from the
            // newly-available full pool.
            cycleState.CycleNumber += 1;
            cycleState.UsedInCycleCount = 0;
            cycleState.LastCycleCompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            availableThisCycle = eligiblePlayerIds.ToList();
        }

        // REQ-1209: the eligible pool size as observed at this generation —
        // updated every generation, rollover or not.
        cycleState.ObservedPoolSize = eligiblePlayerIds.Count;

        var targetPlayerIds = PickDistinct(availableThisCycle, template.PuzzleCount);

        cycleState.UsedInCycleCount += targetPlayerIds.Count;

        // ADR-0054: refresh exactly these N targets' PlayerCareerStint rows
        // from Wikidata's full career history, before anyone can view the
        // puzzle — eligibility above was already decided from whatever xG
        // Grid byproduct data existed BEFORE this call, deliberately (see
        // IPlayerCareerStintRefreshService's own doc comment for why this
        // can enrich an already-selected target's own clues but can never
        // retroactively change who was eligible for this generation). Never
        // throws — a Wikidata failure here must not fail round generation,
        // same REQ-103 reasoning xG Grid's own generation-time lookups
        // follow.
        await careerStintRefreshService.RefreshCareerStintsAsync(targetPlayerIds, cancellationToken);

        var instanceId = Guid.NewGuid();
        var instance = new PathInstance
        {
            Id = instanceId,
            TemplateId = template.Id,
            // PathInstanceId set explicitly rather than left to EF Core's
            // relationship fixup via this navigation — same reasoning as
            // GridGameModule.GenerateInstanceAsync's own GridInstanceId
            // assignment.
            Puzzles = targetPlayerIds.Select(playerId => new PathPuzzle
            {
                Id = Guid.NewGuid(),
                PathInstanceId = instanceId,
                TargetPlayerId = playerId,
            }).ToList(),
        };

        // REQ-1208: the PathInstance/PathPuzzle write and the cycle-usage
        // write (cycleState + one PathCycleTargetUsage row per target) go
        // through in the same unit of work — see
        // AddInstanceWithCycleUsageAsync's own doc comment for why.
        await pathInstanceRepository.AddInstanceWithCycleUsageAsync(instance, cycleState, targetPlayerIds, cancellationToken);

        return new GameInstance { Id = instance.Id };
    }

    // REQ-1204: correctness is a direct PlayerId match against this puzzle's
    // one specific target — no category-membership check, unlike
    // GridGameModule.ScoreSubmissionAsync (REQ-203). userId is unused here,
    // same as it's effectively unused as a correctness input in
    // GridGameModule.ScoreSubmissionAsync too (only used there for a
    // disambiguation log line) — xG Path's correctness never depends on
    // which user is guessing.
    //
    // Judgment call (flagged for architecture-reviewer): deliberately no
    // fuzzy-matching stage and no REQ-209-style disambiguation prompt here,
    // unlike GridGameModule.FindMatchAsync's three-stage (exact/alias/fuzzy)
    // pipeline. Grid's fuzzy stage bounds its candidate pool via
    // GetPlayersWithEitherAttributeAsync — players already known (via a
    // cached PlayerAttribute row) to satisfy one of the cell's two
    // *categories*. xG Path has no category concept to bound a fuzzy search
    // by: building an equivalent bound would mean either a new,
    // category-less repository method (real new matching infrastructure,
    // which REQ-1204's own text and docs/backlog.md's S-082 entry both rule
    // out — "no new matching infrastructure for this game") or an unbounded
    // full-table fuzzy scan on every guess, which this codebase's existing
    // fuzzy pass deliberately avoids for Grid too. Disambiguation is also
    // unnecessary here for a structural reason, not just a scope cut:
    // unlike Grid (where multiple *different* players can each
    // independently satisfy both of a cell's categories), an xG Path
    // puzzle's correctness only ever cares whether the ONE specific target
    // is among the name-matched candidates — additional same-named
    // non-target players resolving alongside the real target changes
    // nothing about the outcome, so there is nothing to disambiguate.
    public async Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        var guessSubmission = (GuessSubmission)submission;

        var instance = await pathInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new PathScoringException($"PathInstance '{instanceId}' not found.");

        var puzzle = instance.Puzzles.FirstOrDefault(p => p.Id == guessSubmission.CellId)
            ?? throw new PathScoringException($"Puzzle '{guessSubmission.CellId}' not found in path instance '{instanceId}'.");

        // REQ-1204/ADR-0007: same PlayerNameIndex-adjacent matching pipeline
        // xG Grid guesses use for correctness resolution — Player.
        // NormalizedFullName first, then PlayerAlias.NormalizedAlias, same
        // order/normalization GridGameModule.FindMatchAsync uses (minus the
        // fuzzy stage, see this method's own comment above).
        var normalized = PlayerNameNormalizer.Normalize(guessSubmission.SubmittedName);

        var candidates = await playerRepository.GetPlayersByNormalizedFullNameAsync(normalized, cancellationToken);
        if (candidates.Count == 0)
            candidates = await playerAliasRepository.GetPlayersByNormalizedAliasAsync(normalized, cancellationToken);

        var isCorrect = candidates.Any(c => c.Id == puzzle.TargetPlayerId);

        return isCorrect
            ? new ScoreResult { IsCorrect = true, PlayerAnswerId = puzzle.TargetPlayerId }
            : new ScoreResult { IsCorrect = false };
    }

    // ADR-0021-equivalent: round-close's unanswered-cell penalty needs
    // every cell id for the instance, regardless of whether anyone ever
    // guessed it — same contract GridGameModule.GetCellIdsAsync already
    // fulfills.
    public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await pathInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new PathScoringException($"PathInstance '{instanceId}' not found.");

        return instance.Puzzles.Select(p => p.Id).ToList();
    }

    // ADR-0041/REQ-1205: every xG Path puzzle shares the same fixed
    // allowance (7, matching REQ-1203's fixed 7-turn clue sequence 1:1) —
    // no repository lookup, no branching on instanceId or cellId, same
    // "pure extraction" shape as GridGameModule.GetMaxAttemptsForCellAsync.
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        Task.FromResult(MaxAttemptsPerPuzzle);

    // REQ-215/ADR-0052 (S-089, architecture-review fix): xG Path has no
    // "row/col category" concept to return here at all — a PathPuzzle's
    // correctness is a single fixed TargetPlayerId, not two independent
    // category axes a candidate must satisfy (see ScoreSubmissionAsync's own
    // doc comment above, and PathPuzzle's own doc comment). Nothing in this
    // story or its frontend wires REQ-215's suggestion entry point up for xG
    // Path — SuggestionEndpoints only ever resolves a round whose GameKey is
    // "xg-grid" today — so there is no real caller that can reach this
    // implementation in production. Judgment call (flagged for
    // architecture-reviewer, mirroring ScoreSubmissionAsync's own flagged
    // judgment call above): throws NotSupportedException rather than
    // returning null or a fabricated pair of empty-string categories, since
    // either of those would let a future caller silently misrepresent this
    // puzzle's shape instead of getting a loud signal that xG Path's own
    // suggestion flow (if ever built) needs real design work here, not a
    // default. Deliberately NOT derived from GameEntityNotFoundException —
    // this isn't "the id didn't resolve" (GetCellIdsAsync/
    // ScoreSubmissionAsync's exception, which this method could equally have
    // thrown for an unresolved puzzleId, but there is no reachable caller to
    // exercise that distinction yet) — it's "this game has no such concept,"
    // a different failure mode that a bare 404 would misrepresent if it were
    // ever caught the same way.
    public Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Path puzzles have no row/col category concept — REQ-215's PlayerSuggestion flow is not supported for xg-path.");

    // REQ-216/ADR-0057: xG Path is out of scope for this feature
    // (docs/backlog.md S-094 scopes it to xG Grid only) — returns null
    // unconditionally rather than fabricating a value. Unlike
    // GetCellCategoryTypesAsync above, this isn't "xG Path has no such
    // concept" (a wrong-but-real guess against a PathPuzzle is just as real
    // a case as against a GridCell) — it's simply not built for this game
    // yet, and GuessSubmissionService's own null-means-no-identity contract
    // already handles that correctly: returning null here leaves xG Path's
    // existing incorrect-guess display completely unaffected, exactly as if
    // this REQ didn't exist for this game at all.
    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WrongGuessPlayerInfo?>(null);

    // REQ-710/S-201: xG Path's only per-user table is Guess, which is
    // Core.Scoring's OWN entity (COMP-04) — AccountDeletionService already
    // anonymizes it directly via IGuessRepository before ever reaching this
    // loop (see IGameModule.PurgeUserDataAsync's own doc comment). xG Path
    // itself (PathInstance/PathPuzzle/PathCycleState/PathCycleTargetUsage)
    // owns no per-user row at all, so there is nothing left here for this
    // module to purge — a genuine no-op, not a deferred TODO, mirroring
    // GridGameModule.PurgeUserDataAsync's own identical reasoning.
    public Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // REQ-1202: pick `count` distinct entries from `pool` uniformly at
    // random, without replacement — Fisher-Yates-style pick, using the
    // injected _random rather than GridGameModule's static Shuffle helper
    // (which always uses Random.Shared) so tests can pin selection.
    private List<Guid> PickDistinct(IReadOnlyList<Guid> pool, int count)
    {
        var remaining = new List<Guid>(pool);
        var selected = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var index = _random.Next(remaining.Count);
            selected.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        return selected;
    }
}
