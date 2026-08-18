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
// see GetEligiblePlayerIdsAsync's own doc comment. ADR-0074/S-138
// (Epic 12) tightened REQ-1201's own eligibility rule from "≥3 documented
// stint rows, any clubs, plus ≥1 stint at a qualifying seeded club" to
// "≥2 DISTINCT qualifying seeded clubs" — see IsEligible's own doc comment
// for the exact current rule.
public class XGPathGameModule(
    IPathInstanceRepository pathInstanceRepository,
    // S-106/S-107 (pure refactor): the sibling repositories carrying the
    // methods split out of the original, now-deleted IPlayerStoreRepository
    // — see ADR-0067. playerCareerStintRepository carries
    // GetCareerStintCandidatePlayerIdsAsync/GetCareerStintsByPlayerIdsAsync.
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    IPlayerAliasRepository playerAliasRepository,
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

    // REQ-1201/ADR-0074/S-138: eligibility now requires ≥2 distinct
    // QUALIFYING seeded clubs, not just 1 (ADR-0047's old threshold). Two
    // rows at the SAME seeded club (a loan, then a later permanent return)
    // count as one qualifying club, not two — see IsEligible's own comment
    // for the exact "distinct club NAMES, not stint rows" semantics this
    // constant enforces. Named here, not a bare literal, because the
    // narrowing pass below (GetEligiblePlayerIdsAsync's
    // GetCareerStintCandidatePlayerIdsAsync call) needs the exact same
    // threshold IsEligible itself uses — one named constant, not two
    // independent magic 2s that could drift apart.
    private const int MinQualifyingSeededClubs = 2;

    // REQ-1203/ADR-0074/S-138 (architecture-review finding, not the
    // original backlog text): a total-stint-row floor is STILL required
    // here, independent of MinQualifyingSeededClubs above — it is NOT the
    // same check ADR-0045's original "≥3 distinct documented career club
    // stints" reasoning established, and is deliberately NOT dropped as
    // "redundant" the way the backlog story assumed. Reason:
    // PathClueSequenceBuilder.SplitIntoTurns divides a target's full stint
    // count N across exactly 3 fixed club-reveal turns and assumes N >= 3
    // (REQ-1203) — for N=2 it produces turn sizes [0, 1, 1], silently
    // showing the player ZERO clubs on the first club-reveal turn. Since
    // MinQualifyingSeededClubs (2) only bounds the number of QUALIFYING
    // SEEDED stints, not a candidate's TOTAL documented stint count, a
    // real player with exactly 2 total stints (both at qualifying seeded
    // clubs, no third unseeded stint) would otherwise pass eligibility and
    // break REQ-1203's turn split. This floor and MinQualifyingSeededClubs
    // are two independent, both-required conditions — see IsEligible's own
    // comment.
    private const int MinDocumentedStintCount = 3;

    // REQ-1201/ADR-0073/S-137: xG Path's own, additive floor — deliberately
    // separate from, and narrower than, REQ-112's shared 1939 pool floor
    // (enforced far upstream at Wikidata SPARQL query time, WikidataClient's
    // BuildPlayerPoolBirthYearQuery/ADR-0025, shared with xG Grid). Living
    // here as a Player-level check rather than inside PathCareerStintFilter
    // is deliberate: BirthYear is a fact about the PLAYER (Player.BirthYear,
    // REQ-1207), not about any individual PlayerCareerStint row, so it has
    // no natural home in a stint-level filter. See ADR-0073 for why this
    // isn't instead a shared SPARQL-level change (would also narrow xG
    // Grid's pool, out of scope — same reasoning Epic 12's intro in
    // docs/backlog.md already gives for why the 1939 floor couldn't simply
    // be raised in place).
    private const int MinBirthYear = 1975;

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
    //
    // REQ-1201/ADR-0073/S-137: BirthYear >= MinBirthYear (1975) IS checked
    // here, below — this is NOT a re-check of REQ-112. It's a separate,
    // narrower, xG-Path-only floor with no upstream enforcement anywhere
    // (unlike REQ-112, nothing filters this at Wikidata-query time), so
    // unlike REQ-112 above it cannot be treated as "already guaranteed by
    // construction." See MinBirthYear's own comment and ADR-0073.
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
        // a cheap (PlayerId, ClubName)-only read — "at least
        // MinDocumentedStintCount (3) total rows AND at least
        // MinQualifyingSeededClubs (2) distinct seeded club names among a
        // player's stints" (ignoring the appearance-count sub-condition,
        // which only narrows further, since that projection doesn't carry
        // AppearanceCount) is computable from that projection alone and is
        // a true superset of IsEligible's actual candidates — it never
        // excludes one IsEligible would have accepted (see
        // GetCareerStintCandidatePlayerIdsAsync's own doc comment, REQ-1201/
        // REQ-1203/ADR-0074/S-138) — then load full stint data (all
        // columns, needed for the date-order and per-club appearance-count
        // checks) only for that narrowed set.
        var candidateIds = await playerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync(
            seededClubNames, MinDocumentedStintCount, MinQualifyingSeededClubs, cancellationToken);
        var stintsByPlayer = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(candidateIds, cancellationToken);

        // Bug fix (2026-08-08, REQ-1203): leftover pre-2026-08-02
        // youth-national-team rows (see PathCareerStintFilter's own doc
        // comment) are excluded here too, not just at the display path.
        // A junk row is never itself a club present in seededClubNames, so
        // (post-S-138) it can't directly manufacture a qualifying club that
        // wasn't real — but it still carries its own (StartYear, EndYear),
        // and an unfiltered junk row could coincidentally collide with a
        // real stint's date pair and cause IsEligible's order-determinable
        // check to spuriously fail a genuinely eligible candidate.
        // GetCareerStintCandidatePlayerIdsAsync's own raw-row narrowing
        // pass above is deliberately left unfiltered — it's documented as
        // a true, over-inclusive SUPERSET of IsEligible's real candidates
        // (a candidate it lets through but IsEligible then rejects is
        // exactly the intended, safe shape of that narrowing pass; it
        // would only be a bug if it excluded a genuinely eligible
        // candidate, which not filtering here never does).
        // S-139 (2026-08-18, REQ-1203/ADR-0075): ExcludeBTeams is chained
        // alongside ExcludeNationalTeams for the same reason — a leftover
        // B-team/reserve-team row (e.g. "Real Madrid Castilla") is not
        // itself a seeded club either, but can still collide on dates the
        // same way a national-team row can.
        var structurallyEligibleIds = stintsByPlayer
            .Where(kvp => IsEligible(
                PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(kvp.Value)),
                seededClubNames))
            .Select(kvp => kvp.Key)
            .ToList();

        // REQ-1201/ADR-0073/S-137: BirthYear >= MinBirthYear (1975),
        // applied here rather than inside IsEligible/PathCareerStintFilter
        // because it's a fact about the PLAYER (Player.BirthYear), not
        // about any individual PlayerCareerStint row — stints have no
        // BirthYear of their own. Runs against exactly the structurally-
        // eligible set computed above, before familiarity filtering below,
        // mirroring ADR-0056's own "familiarity filter only sees
        // structurally-eligible candidates" ordering — no point spending a
        // familiarity-check call on a candidate this check would already
        // exclude.
        //
        // Fail-closed (ADR-0073, matching ADR-0070's precedent): a
        // candidate whose Player.BirthYear is null is EXCLUDED, not
        // included — xG Path cannot verify a null-BirthYear candidate
        // meets the new floor, and silently admitting it would be exactly
        // the "admit what can't be verified" failure mode ADR-0070/REQ-211's
        // fallback deliberately avoid elsewhere in this codebase. HasValue
        // check is required (not just `BirthYear >= MinBirthYear`, which
        // would evaluate false for null and coincidentally produce the
        // same excluded outcome) purely so this reads as an explicit
        // decision rather than an accident of nullable-int comparison
        // semantics.
        var playersById = await playerRepository.GetPlayersByIdsAsync(structurallyEligibleIds, cancellationToken);
        var birthYearEligibleIds = structurallyEligibleIds
            .Where(id => playersById.TryGetValue(id, out var player) &&
                         player.BirthYear.HasValue &&
                         player.BirthYear.Value >= MinBirthYear)
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
        var familiarIds = await playerFamiliarityService.FilterFamiliarAsync(birthYearEligibleIds, cancellationToken);
        return birthYearEligibleIds.Where(familiarIds.Contains).ToList();
    }

    // REQ-1201/ADR-0074/S-138's three independent structural checks (down
    // from the pre-S-138 shape, but NOT down to two — see
    // MinDocumentedStintCount's own comment: dropping the total-row floor
    // entirely, as the original backlog story assumed was safe, was found
    // during architecture/quality review to break REQ-1203's clue-turn
    // split for a 2-stint candidate, so it is RETAINED here, just
    // re-justified):
    //   - at least MinDocumentedStintCount (3) total documented stint rows,
    //     any clubs — required for REQ-1203's PathClueSequenceBuilder,
    //     which divides a target's full stint count across exactly 3 fixed
    //     club-reveal turns and assumes at least 3. NOT a re-statement of
    //     ADR-0045's original "3 distinct documented career club stints"
    //     reasoning (that textual question is now moot — REQ-1201's own
    //     rule no longer hinges on a literal "3" from REQ-1201's text) —
    //     this floor exists purely so every eligible target has enough
    //     documented career data for REQ-1203 to build a real 3-turn club
    //     reveal, independent of REQ-1201's own club-quality signal below.
    //   - "chronological order determinable from start/end dates": rejects
    //     a candidate if any two of their stints share an identical
    //     (StartYear, EndYear) pair (including two simultaneously "ongoing"
    //     stints, EndYear both null) — at that point
    //     IPlayerStoreRepository.AddCareerStintsAsync's persisted
    //     SequenceOrder between those two rows is an artifact of write
    //     order, not something actually derivable from the dates
    //     themselves, so "order determinable from start/end dates" fails
    //     for this candidate. Unchanged by S-138.
    //   - at least MinQualifyingSeededClubs (2) DISTINCT clubs present in
    //     the seeded ClubDefinition reference table (REQ-109), each
    //     individually meeting the appearance-count bar: at least
    //     MinAppearancesAtSeededClub games played there when that count is
    //     known (ADR-0047), or AppearanceCount unknown (a stint with no
    //     recorded AppearanceCount still counts, since "unknown" is not
    //     evidence of a fringe appearance; only a known, sub-threshold
    //     count disqualifies that stint). The count is over distinct
    //     qualifying club NAMES, not stint rows — a player with many stints
    //     at one seeded club (e.g. a loan, then a later permanent return)
    //     still only contributes ONE qualifying club, not two. Extra stints
    //     at non-seeded clubs, or at seeded clubs that individually fail
    //     the appearance bar, don't block eligibility as long as
    //     MinQualifyingSeededClubs distinct seeded clubs DO qualify.
    //   - ADR-0056: and, on top of the three structural checks above, the
    //     candidate is judged "familiar enough" via
    //     IPlayerFamiliarityService.FilterFamiliarAsync (see
    //     GetEligiblePlayerIdsAsync below) — none of the three checks here
    //     says anything about whether a player is one a casual player would
    //     recognize.
    private static bool IsEligible(IReadOnlyList<PlayerCareerStint> stints, IReadOnlySet<string> seededClubNames)
    {
        if (stints.Count < MinDocumentedStintCount)
            return false;

        var datePairs = stints.Select(s => (s.StartYear, s.EndYear)).ToList();
        if (datePairs.Count != datePairs.Distinct().Count())
            return false;

        var qualifyingSeededClubCount = stints
            .Where(s =>
                seededClubNames.Contains(s.ClubName) &&
                (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub))
            .Select(s => s.ClubName)
            .Distinct()
            .Count();

        return qualifyingSeededClubCount >= MinQualifyingSeededClubs;
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
