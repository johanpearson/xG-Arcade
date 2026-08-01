using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPath;

// COMP-11: IGameModule implementation for xG Path, the second game hosted
// on the platform. S-080 scaffolded the module boundary only; S-081
// implemented REQ-1201 (target-player eligibility) and REQ-1202 (round
// structure — a small, fixed set of distinct-target puzzles). This story
// (S-082) implements REQ-1204 (guess correctness resolution) and REQ-1205
// (per-puzzle attempt cap, fixed at 7), mirroring GridGameModule's
// (COMP-05) established "assemble instance, persist via repository, return
// GameInstance" shape for generation, and its
// GuessSubmission-cast/name-resolution shape for scoring.
public class XGPathGameModule(
    IPathInstanceRepository pathInstanceRepository,
    IPlayerStoreRepository playerStoreRepository,
    ICategoryValueRepository categoryValueRepository,
    Random? random = null) : IGameModule
{
    public const string XGPathGameKey = "xg-path";

    // REQ-1201/ADR-0047: a seeded-club stint only counts toward eligibility
    // if it reflects meaningful playing time there, not a one-off loan/
    // fringe appearance — see the ADR for why 20 and why an unknown count
    // still passes rather than being rejected.
    private const int MinAppearancesAtSeededClub = 20;

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

    public string GameKey => XGPathGameKey;

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await pathInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new PathGenerationException($"PathTemplate '{config.TemplateId}' not found.");

        var eligiblePlayerIds = await GetEligiblePlayerIdsAsync(cancellationToken);

        // REQ-1202: exactly N puzzles, never fewer — an insufficient pool
        // is a hard abort, not a silently-smaller instance.
        if (eligiblePlayerIds.Count < template.PuzzleCount)
        {
            throw new PathGenerationException(
                $"Not enough eligible target players to build a {template.PuzzleCount}-puzzle xG Path instance " +
                $"({eligiblePlayerIds.Count} eligible players available).");
        }

        var targetPlayerIds = PickDistinct(eligiblePlayerIds, template.PuzzleCount);

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

        await pathInstanceRepository.AddInstanceAsync(instance, cancellationToken);

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
        var stintsByPlayer = await playerStoreRepository.GetAllCareerStintsByPlayerAsync(cancellationToken);
        var seededClubNames = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => c.Name)
            .ToHashSet();

        return stintsByPlayer
            .Where(kvp => IsEligible(kvp.Value, seededClubNames))
            .Select(kvp => kvp.Key)
            .ToList();
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
    private static bool IsEligible(IReadOnlyList<PlayerCareerStint> stints, IReadOnlySet<string> seededClubNames)
    {
        if (stints.Count < 3)
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
