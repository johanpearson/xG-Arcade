using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// ADR-0100: LeaderboardService (Core.Leagues, COMP-02) sources every
// scope's round totals through a per-GameKey IRoundScoreSource instead of
// calling IGuessRepository/ILiveRoundContributionService directly — the
// same structural fix ScoreLockingService.MaterializeUnansweredCellsAsync
// already applied for round-close (ADR-0021), now applied to leaderboard
// reads. "xg-predict" never writes a Guess row (ADR-0096); its totals live
// in PredictMatchPrediction, reached only through
// IPredictInstanceRepository (Games.XGPredict/COMP-15's own persistence).
// Core.Leagues/Core.Scoring must never reference IPredictInstanceRepository,
// PredictInstance, PredictMatch, or PredictMatchPrediction directly — always
// through the IRoundScoreSource resolved for that Round's GameKey. If a
// future change seems to need that reference, stop and flag it rather than
// adding it (ADR-0100's "For AI agents" section).
//
// Every method below takes already-resolved Round/User entities (Core's own
// types), never a bare Guid the implementation would have to re-resolve —
// LeaderboardService (which already injects IRoundRepository/IUserRepository)
// is the only thing that ever resolves Round/User data; a resolved
// IRoundScoreSource just reads Round.GameInstanceId/Round.GameKey/
// User.IsGuest off what it's handed. No implementation of this interface
// may inject IRoundRepository or IUserRepository itself — if one seems to
// need to, that's a sign the caller should be resolving and passing more,
// not that this rule should bend.
//
// This interface's own signature must never mention PredictInstance/
// PredictMatchPrediction/PredictMatch, or any other game-specific type —
// that is the whole point of it living in Core.Scoring.
public interface IRoundScoreSource
{
    // REQ-409/411: per-round totals for each requested user, across every
    // *qualifying* round for this source's GameKey(s). closedRounds is
    // every closed Round for the GameKey(s) this source serves, resolved
    // by the caller (LeaderboardService already owns IRoundRepository);
    // members carries each candidate user's IsGuest/ClaimedAt so
    // REQ-717/ADR-0036 eligibility can be applied uniformly by whichever
    // implementation needs it. A user with zero qualifying rounds is
    // absent from the result (never present with an empty list) — same
    // "absent, not defaulted" convention IGuessRepository's existing
    // method already uses.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundTotalsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Round> closedRounds,
        IReadOnlyCollection<User> members,
        CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true);

    // REQ-406/407: the active round's current per-participant total.
    Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default);

    // REQ-408: one closed round's totals.
    Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default);

    // REQ-405: totals summed across a set of closed rounds (a calendar
    // window).
    Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default);

    // REQ-305: did anyone participate in this round at all, regardless of
    // points earned — used by RoundGenerationService to decide whether an
    // unplayed round's schedule should be extended instead of generating a
    // new one. Distinct from GetTotalsByRoundAsync's "how many points"
    // shape: a round with participants but zero points everywhere (if
    // that's even possible for a given GameKey) must still count as
    // played. Every implementation must answer this honestly off its own
    // GameKey's data shape (never a Guess-row assumption that only holds
    // for GuessRoundScoreSource's own GameKeys) — the earlier version of
    // this check called IGuessRepository directly from
    // RoundGenerationService, which was wrong for "xg-higher-lower" (it
    // structurally never writes Guess rows, so that always-empty read
    // would have made its round renew forever); routed through this
    // per-GameKey abstraction instead, same as every other scope read.
    Task<bool> HasAnyParticipantAsync(Round round, CancellationToken cancellationToken = default);
}
