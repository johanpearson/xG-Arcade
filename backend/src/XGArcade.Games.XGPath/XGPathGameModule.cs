using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGPath;

// COMP-11: IGameModule implementation for xG Path, the second game hosted
// on the platform. S-080 scaffolded the module boundary only; S-081
// implemented REQ-1201 (target-player eligibility) and REQ-1202 (round
// structure — a small, fixed set of distinct-target puzzles). This story
// (S-082) implements REQ-1204 (guess correctness resolution) and REQ-1205
// (per-puzzle attempt cap, fixed at 7), mirroring GridGameModule's
// (COMP-05) established "assemble instance, persist via repository, return
// GameInstance" shape for generation, and its
// GuessSubmission-cast/name-resolution shape for scoring. ADR-0056
// (2026-08-02, bug-bundle) added the IPlayerFamiliarityService dependency —
// see GetEligiblePlayerIdsAsync's own doc comment.
public class XGPathGameModule(
    IPathInstanceRepository pathInstanceRepository,
    IPlayerStoreRepository playerStoreRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerCareerStintRefreshService careerStintRefreshService,
    IPlayerFamiliarityService playerFamiliarityService,
    Random? random = null,
    TimeProvider? timeProvider = null) : IGameModule
{
    public const string XGPathGameKey = "xg-path";

    // REQ-1201/ADR-0047: a seeded-club stint only counts toward eligibility
    // if it reflects meaningful playing time there, not a one-off loan/
    // fringe appearance — see the ADR for why 20 and why an unknown count
    // still passes rather than being rejected.
    private const int MinAppearancesAtSeededClub = 20;

    // REQ-1201: "3 distinct documented career club stints" (read as 3
    // stint ROWS, not 3 distinct clubs — see IsEligible's own comment).
    // Named here, not a bare literal, because the perf fix below
    // (GetEligiblePlayerIdsAsync's narrowing pass) needs the exact same
    // threshold as IsEligible's own `stints.Count < MinStintCount` check —
    // one named constant, not two independent magic 3s that could drift
    // apart.
    private const int MinStintCount = 3;

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

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await pathInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new PathGenerationException($"PathTemplate '{config.TemplateId}' not found.");

        var eligiblePlayerIds = await GetEligiblePlayerIdsAsync(cancellationToken);

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

        var candidates = await playerStoreRepository.GetPlayersByNormalizedFullNameAsync(normalized, cancellationToken);
        if (candidates.Count == 0)
            candidates = await playerStoreRepository.GetPlayersByNormalizedAliasAsync(normalized, cancellationToken);

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

    // REQ-1201: candidate eligibility. Reads only PlayerCareerStint (via
    // IPlayerStoreRepository, boundary rule 1 — Games.XGPath never touches
    // XGArcadeDbContext directly) and ClubDefinition (via
    // ICategoryValueRepository, the same call GridGameModule already makes
    // for REQ-109's club reference data) — never PlayerAttribute/
    // PlayerOverride, which remain xG Grid's own correctness-checking path
    // only (ADR-0042/PlayerCareerStint's own doc comment: "xG Grid's
    // correctness-checking path must NEVER read this table").
    //
    // REQ-112 pool membership (male, born 1939 or later) is deliberately
    // NOT re-checked here: Player has no Gender field at all, and while
    // Player.BirthYear now exists (REQ-1207/S-082, for xG Path's own
    // age/birth-year clue, not for pool filtering), re-deriving REQ-112's
    // pool membership from it here would duplicate a check that's already
    // structurally guaranteed — the restriction is enforced entirely
    // upstream, at Wikidata-query time (WikidataClient's
    // BuildCountryClubIntersectionQuery/BuildClubClubIntersectionQuery/
    // BuildPlayerPoolBirthYearQuery, all filtering on P21/P569 before
    // anything is ever persisted as a Player row — ADR-0025). Every
    // Player/PlayerCareerStint row already satisfies REQ-112 by
    // construction, the same reasoning GridGameModule itself relies on for
    // not re-checking this at runtime either.
    private async Task<IReadOnlyList<Guid>> GetEligiblePlayerIdsAsync(CancellationToken cancellationToken)
    {
        var seededClubNames = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => c.Name)
            .ToHashSet();

        // Perf fix (NOTES.md 2026-08-03): PlayerCareerStint has grown to
        // ~608K rows (ADR-0055's prefetch-player-careers job) and keeps
        // growing as more countries are added, so a full
        // GetAllCareerStintsByPlayerAsync-style read on every round
        // generation no longer scales. Narrow to real candidates first with
        // a cheap (PlayerId, ClubName)-only read — IsEligible's >= 3-stint-
        // row check and "any stint at a seeded club at all" (ignoring the
        // appearance-count sub-condition, which only narrows further) are
        // both necessary-but-not-sufficient conditions computable from that
        // projection alone, so this is a true superset of IsEligible's
        // actual candidates and never excludes one it would have accepted
        // (see GetCareerStintCandidatePlayerIdsAsync's own doc comment) —
        // then load full stint data (all columns, needed for the date-order
        // and appearance-count checks) only for that narrowed set.
        var candidateIds = await playerStoreRepository.GetCareerStintCandidatePlayerIdsAsync(
            seededClubNames, MinStintCount, cancellationToken);
        var stintsByPlayer = await playerStoreRepository.GetCareerStintsByPlayerIdsAsync(candidateIds, cancellationToken);

        // Bug fix (2026-08-08, REQ-1203): leftover pre-2026-08-02
        // youth-national-team rows (see PathCareerStintFilter's own doc
        // comment) are excluded here too, not just at the display path —
        // otherwise a player whose real documented career is fewer than
        // MinStintCount (3) club stints could still pass IsEligible's own
        // `stints.Count < MinStintCount` check purely on the strength of
        // leftover junk rows padding the count. GetCareerStintCandidatePlayerIdsAsync's
        // own raw-row narrowing pass above is deliberately left unfiltered
        // — it's documented as a true, over-inclusive SUPERSET of
        // IsEligible's real candidates (a candidate it lets through but
        // IsEligible then rejects is exactly the intended, safe shape of
        // that narrowing pass; it would only be a bug if it excluded a
        // genuinely eligible candidate, which not filtering here never
        // does).
        var structurallyEligibleIds = stintsByPlayer
            .Where(kvp => IsEligible(PathCareerStintFilter.ExcludeNationalTeams(kvp.Value), seededClubNames))
            .Select(kvp => kvp.Key)
            .ToList();

        // ADR-0056: a real player-facing complaint ("I got this Austrian guy
        // I had no idea who he is") — the three structural checks above say
        // nothing about whether a candidate is actually recognizable, so a
        // long-but-obscure career passed them just as easily as a star's.
        // FilterFamiliarAsync never shrinks the pool below what's safe to
        // shrink to on its own (it fails open on a Wikidata failure or a
        // total data gap — see its own doc comment) — GenerateInstanceAsync's
        // existing "not enough eligible players" abort still covers the case
        // where familiarity filtering leaves too few candidates.
        var familiarIds = await playerFamiliarityService.FilterFamiliarAsync(structurallyEligibleIds, cancellationToken);
        return structurallyEligibleIds.Where(familiarIds.Contains).ToList();
    }

    // REQ-1201's three independent checks:
    //   - at least 3 documented stint ROWS, not 3 distinct clubs.
    //     PlayerCareerStint's own doc comment explicitly allows two rows at
    //     the same club (a loan, then a later permanent return) as two
    //     distinct, valid stints; REQ-1201's text says "3 distinct
    //     documented career club stints", read here as 3 separately
    //     recorded stint rows, not 3 different clubs.
    //   - "chronological order determinable from start/end dates": rejects
    //     a candidate if any two of their stints share an identical
    //     (StartYear, EndYear) pair (including two simultaneously "ongoing"
    //     stints, EndYear both null) — at that point
    //     IPlayerStoreRepository.AddCareerStintsAsync's persisted
    //     SequenceOrder between those two rows is an artifact of write
    //     order, not something actually derivable from the dates
    //     themselves, so "order determinable from start/end dates" fails
    //     for this candidate.
    //   - at least one stint at a club present in the seeded
    //     ClubDefinition reference table (REQ-109), with at least
    //     MinAppearancesAtSeededClub games played there when that count is
    //     known (ADR-0047) — a stint with no recorded AppearanceCount still
    //     counts, since "unknown" is not evidence of a fringe appearance;
    //     only a known, sub-threshold count disqualifies a stint.
    //   - ADR-0056: and, on top of the three structural checks above, the
    //     candidate is judged "familiar enough" via
    //     IPlayerFamiliarityService.FilterFamiliarAsync (see
    //     GetEligiblePlayerIdsAsync below) — none of the three checks here
    //     say anything about whether a player is one a casual player would
    //     recognize.
    private static bool IsEligible(IReadOnlyList<PlayerCareerStint> stints, IReadOnlySet<string> seededClubNames)
    {
        if (stints.Count < MinStintCount)
            return false;

        var datePairs = stints.Select(s => (s.StartYear, s.EndYear)).ToList();
        if (datePairs.Count != datePairs.Distinct().Count())
            return false;

        return stints.Any(s =>
            seededClubNames.Contains(s.ClubName) &&
            (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub));
    }

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
