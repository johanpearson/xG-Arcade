using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGConnect.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as XGArcade.Games.XGPath.Tests' own
// FakePlayerCareerStintRefreshService, ADR-0054). Replaces this project's
// pre-refactor FakeWikidataClient now that PlayerCareerOverlapService
// delegates to IPlayerCareerStintRefreshService directly rather than
// IWikidataClient (2026-09-02, S-211 architecture-review follow-up).
//
// Unlike Games.XGPath.Tests' own fake (which never actually writes
// PlayerCareerStint rows — no xG Path test needs the refreshed data visible
// afterward), this one DOES persist configured stints via the real
// IPlayerCareerStintRepository: HaveSharedClubOverlapAsync re-reads from
// that repository immediately after a refresh call, so a fake that didn't
// write through it couldn't prove the "fetch once, use it in the same call"
// round trip PlayerCareerOverlapServiceTests asserts.
public class FakePlayerCareerStintRefreshService(IPlayerCareerStintRepository playerCareerStintRepository)
    : IPlayerCareerStintRefreshService
{
    private readonly Dictionary<Guid, List<PlayerCareerStint>> _stintsByPlayerId = new();
    private int _remainingFailures;

    // Every batch requested, in call order, as the exact player-ID list
    // passed in — lets a test assert both the batch size and which players
    // were grouped together (or NOT grouped together — e.g. "only the
    // player needing refresh, not the one already cached").
    public List<IReadOnlyList<Guid>> Calls { get; } = [];

    public void SetCareerStints(Guid playerId, params PlayerCareerStint[] stints) =>
        _stintsByPlayerId[playerId] = [.. stints];

    // The next `batches` calls throw WikidataQueryException instead of
    // succeeding (only when throwOnFailure is true — same "throw only when
    // asked" contract the real service now has) — simulates a technical
    // Wikidata failure, which PlayerCareerOverlapService must translate into
    // LiveLookupUnavailableException, never swallow.
    public void FailNextBatches(int batches) => _remainingFailures = batches;

    public async Task RefreshCareerStintsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default)
    {
        Calls.Add(playerIds);

        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            if (throwOnFailure)
                throw new WikidataQueryException("simulated WDQS failure for a player career-stint batch");
            return;
        }

        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();
        foreach (var playerId in playerIds)
        {
            if (_stintsByPlayerId.TryGetValue(playerId, out var stints) && stints.Count > 0)
                newStintsByPlayerId[playerId] = stints;
        }

        if (newStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);
    }
}
