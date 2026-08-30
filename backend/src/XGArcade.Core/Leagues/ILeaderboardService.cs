using XGArcade.Data.Entities;

namespace XGArcade.Core.Leagues;

// COMP-02 (Core.Leagues)'s first real code (S-011) — REQ-401/404's Tier 0
// slice (the global league only; custom leagues are REQ-402-404, deferred
// per MVP-SCOPE.md). Kept as a Core service rather than inline in the API
// endpoint so aggregation logic across League/User/Guess lives in the
// component that's documented to own it, not the transport layer — same
// thin-endpoint/owning-Core-service shape GuessEndpoints ->
// GuessSubmissionService already establishes.
//
// REQ-607/S-034: paginated per implementation-document.md §6's
// cursor-shaped contract. `cursor` is the last-seen rank (0 meaning "start
// from the top"); the implementation still composes the full member list
// in memory (accepted MVP-scale tradeoff, see the doc) but the *response*
// is bounded to `pageSize` and always carries the requesting user's own
// row so SCREEN-03's sticky "your position" footer never needs a second
// round-trip.
//
// REQ-406/407/408 (2026-07-19, ADR-0031/backlog S-053/S-054) added the
// active-round live scope and the past-closed-round browsing scope
// alongside the original all-time/global method — all three still live
// here rather than in a new service, since they're all "the leaderboard
// screen's data", just different scopes of it.
//
// REQ-409 (2026-07-20, backlog S-060): REPLACES, rather than adds to,
// GetGlobalLeaderboardAsync's own ranking — see that method's doc comment.
public interface ILeaderboardService
{
    // REQ-409 (2026-07-20): ranks members by the median of each player's
    // per-round SUM(FinalPoints) totals across every *qualifying* round
    // they've ever played — a qualifying round is closed (Round.ClosedAt
    // set) AND the player has at least one Guess row in it — gated by a
    // minimum of 5 qualifying rounds to appear on the ranked list at all.
    // This replaces REQ-401/404's old raw SUM(FinalPoints ?? 0) ranking
    // outright, not a new tab alongside it: a pure sum only ever grows the
    // more rounds someone plays, so it measured volume as much as skill; the
    // median doesn't. See REQ-409's own text for the full reasoning.
    //
    // No live component: unlike the old ranking (REQ-406), this method no
    // longer takes an activeRound parameter and never folds in a
    // contribution from the currently active/unlocked round — REQ-409 is
    // explicit that folding a live, still-changing round into a median has
    // no resolved meaning here (which round would count, what per-round
    // figure would represent a round still in progress) and is left to a
    // future requirement if ever wanted. REQ-405's locked-only windows
    // already set this same precedent for a different scope; this method
    // now follows it too. Callers (LeaderboardEndpoints) no longer need to
    // resolve "the currently active round" before calling this method.
    //
    // Excluded, not defaulted: a member with fewer than 5 qualifying rounds
    // (including a member with zero — the old GetUserIdsWithAnyGuessAsync
    // "ever played at all" check this subsumes) is absent from the ranked
    // list entirely, the same "absent, not present with a placeholder"
    // shape the pre-REQ-409 zero-guess exclusion already established —
    // never ranked with a default score of 0, which ADR-0021's lowest-wins
    // model would otherwise treat as the *best* possible score.
    //
    // REQ-410/ADR-0043 (2026-07-27): gained a required gameKey parameter,
    // matching the shape GetActiveRoundLeaderboardAsync/GetClosedRoundsAsync/
    // GetClosedRoundLeaderboardAsync/GetWindowedLeaderboardAsync already had
    // — only rounds whose Round.GameKey matches count towards the median,
    // qualifying-round count, and 5-round minimum. Filters on top of REQ-409's
    // formula, does not change it. gameKey is an opaque string the API layer
    // supplies, same as GetClosedRoundsAsync/GetWindowedLeaderboardAsync
    // (ADR-0003).
    //
    // ADR-0095/REQ-1304 (2026-08-30): like GetActiveRoundLeaderboardAsync/
    // GetClosedRoundLeaderboardAsync/GetWindowedLeaderboardAsync above, this
    // method's ranking (via GetRankedMembersAsync's OrderBy/OrderByDescending
    // on Median) resolves sort direction per GameKey from the resolved
    // IScoringStrategy.LowerIsBetter, not assumed ascending — ADR-0021's
    // golf-style default (lowest median wins) for every GameKey except
    // "xg-predict". See GetRankedMembersAsync's own doc comment.
    Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId,
        string gameKey,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    // REQ-407: participant-only, live, active-round-scoped leaderboard.
    // activeRound must be a real, already-resolved active round — callers
    // (the API layer) are responsible for returning a "no active round"
    // response themselves before ever calling this, mirroring RoundEndpoints'
    // existing REQ-303 pattern; this method has no null-round case to handle.
    //
    // ADR-0095/REQ-1304: sort direction is resolved per GameKey via the
    // resolved IScoringStrategy.LowerIsBetter, not assumed ascending —
    // ADR-0021's golf-style default for every GameKey except "xg-predict".
    Task<LeaderboardPage> GetActiveRoundLeaderboardAsync(
        Guid requestingUserId,
        Round activeRound,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    // REQ-408: paginated list of this game's closed rounds, most recently
    // closed first — gameKey is an opaque string the API layer supplies
    // (e.g. GridGameModule.XGGridGameKey), never a game-specific type
    // reference from this Core service (ADR-0003).
    Task<ClosedRoundListPage> GetClosedRoundsAsync(
        string gameKey,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    // REQ-408: one specific closed round's permanently-locked leaderboard.
    // Distinguishes "round id doesn't exist" from "round exists but hasn't
    // closed yet" via ClosedRoundLeaderboardResult.Status — never silently
    // serves a not-yet-closed round as if it were complete.
    //
    // ADR-0095/REQ-1304: sort direction is resolved per GameKey via the
    // resolved IScoringStrategy.LowerIsBetter, not assumed ascending —
    // ADR-0021's golf-style default for every GameKey except "xg-predict".
    Task<ClosedRoundLeaderboardResult> GetClosedRoundLeaderboardAsync(
        Guid roundId,
        Guid requestingUserId,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    // REQ-405: round/week/month/year resolutions alongside the all-time
    // total above — locked-only (same rule as every other scope here), with
    // week/month/year calendar-aligned in UTC (never rolling windows).
    // gameKey is an opaque string the API layer supplies, same as
    // GetClosedRoundsAsync (ADR-0003). nowUtc is the caller's already-
    // resolved current instant (TimeProvider, never DateTime.UtcNow inside
    // this Core service) used to compute which calendar window is "current".
    //
    // ADR-0095/REQ-1304: sort direction is resolved per GameKey via the
    // resolved IScoringStrategy.LowerIsBetter, not assumed ascending —
    // ADR-0021's golf-style default for every GameKey except "xg-predict".
    Task<LeaderboardPage> GetWindowedLeaderboardAsync(
        Guid requestingUserId,
        string gameKey,
        LeaderboardWindowResolution resolution,
        DateTime nowUtc,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    // REQ-411 (2026-08-24, backlog S-178): one player's stats/profile view,
    // scoped to a single GameKey — rounds played, best (lowest,
    // ADR-0021) single round's FinalPoints total, average FinalPoints, and
    // current all-time rank, all reused from data already computed for the
    // leaderboard rather than a new aggregate path. RoundsPlayed/Best/Average
    // come straight from IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync
    // (REQ-408/409's existing per-round-total/qualifying-round query, called
    // here with a single-element userIds collection, applyGuestEligibilityRules:
    // false) — the exact same "absent from the dictionary means zero
    // qualifying rounds" convention GetGlobalLeaderboardAsync already relies
    // on, except a guest's own rounds count for these three figures per
    // REQ-411's own "Out of scope" text ("a guest's rounds-played/best/
    // average figures are shown the same as a claimed account's"); only
    // Rank still inherits REQ-409/717's guest-eligibility gate, via the
    // unchanged, still-guest-excluding GetRankedMembersAsync path below.
    // Rank reuses the same
    // ranked-member ordering GetGlobalLeaderboardAsync itself produces (a
    // shared private helper extracted from that method) so a player's rank
    // here is never computed by a second, independently-drifting formula —
    // it's the same list, just looked up by this one userId instead of paged.
    //
    // UserStatsResult.HasRoundsPlayed is the "no rounds played" discriminator
    // REQ-411 requires: false means zero qualifying rounds, and
    // Best/Average/Rank are all null (never 0-filled); true means at least
    // one qualifying round, though Rank is still independently nullable —
    // omitted below REQ-409's 5-round qualifying minimum even when the other
    // three figures are present. No privacy toggle: identical for the
    // requesting user's own id and any other userId (see REQ-411's own "Out
    // of scope").
    Task<UserStatsResult> GetUserStatsAsync(
        Guid userId,
        string gameKey,
        CancellationToken cancellationToken = default);
}

// REQ-405: the four leaderboard time-window resolutions — Round is "the
// single most recently closed round for the game" (not an arbitrary one,
// and Tier 0 still has no past-round-browsing UI — REQ-408 is the separate,
// existing "browse any closed round" feature); Week/Month/Year are
// calendar-aligned in UTC (ISO week Mon-Sun, calendar month from the 1st,
// calendar year from Jan 1st), never rolling windows.
public enum LeaderboardWindowResolution
{
    Round,
    Week,
    Month,
    Year,
}

// Rank is 1-based and global (not page-local) — the frontend previously
// derived rank from array index, which breaks once a page can start
// mid-list. TotalPoints is a plain int for every scope this record is
// shared across (REQ-405/406/407/408: all still true sums); for
// GetGlobalLeaderboardAsync specifically (REQ-409, 2026-07-20) it holds that
// member's *median* per-round total rounded to the nearest integer
// (MidpointRounding.AwayFromZero) for an even qualifying-round count's
// average-of-two-middle-values — kept int rather than widened to double to
// keep this shared record's shape unchanged for every other scope; ranking
// itself is computed from the unrounded value inside LeaderboardService, so
// this rounding-for-display never affects sort order or tie-breaking.
public record LeaderboardEntry(int Rank, Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

// RequestingUserEntry is populated whenever the requesting user appears
// anywhere in the ranked list — including when their row falls outside Rows
// for the current page — but is null when they don't appear in the ranked
// list at all. For GetGlobalLeaderboardAsync specifically, that means a
// requesting user with fewer than 5 qualifying rounds (REQ-409, 2026-07-20 —
// includes, but is no longer limited to, a user who has never submitted a
// single Guess): membership alone (every authenticated caller today, via
// signup auto-add, AuthController.cs) no longer guarantees a ranked row.
public record LeaderboardPage(
    IReadOnlyList<LeaderboardEntry> Rows,
    LeaderboardEntry? RequestingUserEntry,
    int? NextCursor,
    bool HasMore);

// REQ-408: one browsable closed round, for the round-selection list. Never
// carries the active/upcoming round (Round.ClosedAt is only ever set once
// RoundCloseService has actually closed it).
// REQ-304: SequenceNumber is a display-only label alongside RoundId — RoundId
// remains the real identifier this list's rows are looked up by.
public record ClosedRoundSummary(Guid RoundId, int SequenceNumber, DateTime StartTime, DateTime EndTime, DateTime ClosedAt);

public record ClosedRoundListPage(
    IReadOnlyList<ClosedRoundSummary> Rounds,
    int? NextCursor,
    bool HasMore);

// REQ-408: distinguishes "no such round" from "round exists but hasn't
// closed yet" — both are a real, distinct outcome the API layer must map to
// different status codes, never silently falling through to Found.
public enum ClosedRoundLeaderboardStatus
{
    Found,
    RoundNotFound,
    RoundNotClosedYet,
}

// Page is only populated when Status is Found.
public record ClosedRoundLeaderboardResult(ClosedRoundLeaderboardStatus Status, LeaderboardPage? Page);

// REQ-411: HasRoundsPlayed is the discriminator for the "no rounds played"
// shape — when false, RoundsPlayed is 0 and BestFinalPoints/AverageFinalPoints/Rank
// are all null (never 0-filled). When true, Rank is still independently
// nullable — omitted below REQ-409's 5-round qualifying minimum even though
// the player has qualifying rounds for these other figures.
public record UserStatsResult(
    bool HasRoundsPlayed,
    int RoundsPlayed,
    int? BestFinalPoints,
    double? AverageFinalPoints,
    int? Rank);
