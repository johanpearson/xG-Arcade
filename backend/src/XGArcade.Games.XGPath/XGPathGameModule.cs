using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPath;

// COMP-11: IGameModule implementation for xG Path, the second game hosted
// on the platform. S-080 scaffolded the module boundary only; S-081 (this
// story) implements REQ-1201 (target-player eligibility) and REQ-1202
// (round structure — a small, fixed set of distinct-target puzzles),
// mirroring GridGameModule's (COMP-05) established
// "assemble instance, persist via repository, return GameInstance"
// shape. ScoreSubmissionAsync (REQ-1204) and GetMaxAttemptsForCellAsync
// (REQ-1205) still throw NotImplementedException — that's S-082, not this
// story.
public class XGPathGameModule(
    IPathInstanceRepository pathInstanceRepository,
    IPlayerStoreRepository playerStoreRepository,
    ICategoryValueRepository categoryValueRepository,
    Random? random = null) : IGameModule
{
    public const string XGPathGameKey = "xg-path";

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

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        // REQ-1204 (guess correctness resolution) — see S-082.
        Task.FromException<ScoreResult>(
            new NotImplementedException("xG Path guess scoring not yet implemented — see REQ-1204 (S-082)."));

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

    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        // REQ-1205 (per-puzzle attempt cap, min(stints, 5) + 4) — see S-082.
        Task.FromException<int>(
            new NotImplementedException("xG Path per-puzzle attempt cap not yet implemented — see REQ-1205 (S-082)."));

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
    // NOT checked here: Player has no BirthYear/Gender field at all (see
    // Player.cs) — the restriction is enforced entirely upstream, at
    // Wikidata-query time (WikidataClient's
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
    //     ClubDefinition reference table (REQ-109).
    private static bool IsEligible(IReadOnlyList<PlayerCareerStint> stints, IReadOnlySet<string> seededClubNames)
    {
        if (stints.Count < 3)
            return false;

        var datePairs = stints.Select(s => (s.StartYear, s.EndYear)).ToList();
        if (datePairs.Count != datePairs.Distinct().Count())
            return false;

        return stints.Any(s => seededClubNames.Contains(s.ClubName));
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
