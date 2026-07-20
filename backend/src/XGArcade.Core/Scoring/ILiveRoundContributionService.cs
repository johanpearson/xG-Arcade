using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-406/407, ADR-0031: the one shared per-cell live-contribution formula
// both REQs consume — REQ-406 folds this on top of the shared/per-league
// leaderboard's locked SUM(FinalPoints ?? 0); REQ-407 exposes the exact same
// numbers as their own standalone, active-round-scoped leaderboard. Neither
// caller reimplements this computation.
//
// ADR-0031's "For AI agents" section applies directly here: this is
// recomputed fresh on every call — no memoization, no stored field, nothing
// cached anywhere in this type or its caller. If a future change to this
// class would introduce a cache/snapshot, stop and flag it instead.
public interface ILiveRoundContributionService
{
    // round is the opaque, already-resolved active round (Core.Rounds/
    // COMP-03 entity — the API layer resolves which round is "active" via
    // IRoundRepository.GetActiveByGameKeyAsync using a game-specific GameKey
    // constant it owns, e.g. GridGameModule.XGGridGameKey; this service never
    // does that resolution itself and never references a game-specific type
    // — ADR-0003).
    //
    // Returns one entry per round *participant* (a user with UserId != null
    // on at least one Guess row in this round — the same definition
    // ScoreLockingService.MaterializeUnansweredCellsAsync already uses),
    // even when that participant's live total is 0 — a participant who has
    // attempted a cell but not yet correctly/finally is still a participant
    // and must be distinguishable from a non-participant (who is simply
    // absent from this dictionary), which is why 0 is a real value here and
    // "absent" is a distinct, meaningful case for callers (REQ-407 in
    // particular: absent means "does not appear on this leaderboard at
    // all").
    //
    // Per participant, each of their cells in this round contributes:
    // - a correctly-guessed cell: its current LivePoints (REQ-204's live
    //   uniqueness recomputed via UniquenessCalculator/ScoringRules, exactly
    //   as RoundEndpoints/ScoreLockingService already compute it — never a
    //   third formula)
    // - a locked-incorrect cell (AttemptCount >= GuessRules.MaxAttemptsPerCell,
    //   both attempts used but never correct): ScoringRules.MaxPointsPerCell
    // - a cell the participant has made ZERO guesses on at all (no Guess row
    //   for that cell): ScoringRules.MaxPointsPerCell, same as a
    //   locked-incorrect cell (REQ-406/407, 2026-07-20 — a freshly-initiated
    //   grid's live estimate should start near the theoretical max and count
    //   down as guesses resolve, not sit near zero until every cell is
    //   attempted). This mirrors ScoreLockingService
    //   .MaterializeUnansweredCellsAsync's own round-close behavior for the
    //   same "unanswered cell" case, just computed live instead of
    //   materialized as rows.
    // - a cell attempted once but still incorrect with an attempt remaining
    //   (REQ-210): contributes nothing — deliberately not 0 (ADR-0021's golf
    //   model: 0 is the BEST possible score, and a cell that hasn't resolved
    //   one way or the other yet must not silently count as the best) and
    //   not MaxPointsPerCell either, since it is NOT the same case as a
    //   zero-guess cell above — a genuinely in-progress cell stays
    //   unresolved until its second attempt or round close.
    Task<IReadOnlyDictionary<Guid, int>> GetContributionsByUserIdAsync(
        Round round, CancellationToken cancellationToken = default);
}
