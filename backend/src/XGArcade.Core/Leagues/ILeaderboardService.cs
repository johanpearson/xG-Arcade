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
public interface ILeaderboardService
{
    // activeRound is the API layer's already-resolved "currently active
    // round for this game" (IRoundRepository.GetActiveByGameKeyAsync,
    // resolved with a game-specific GameKey the Api layer owns — never this
    // Core service, per ADR-0003) — null when no round is currently active,
    // in which case this method behaves exactly as it did before REQ-406
    // (locked totals only).
    Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId,
        int cursor,
        int pageSize,
        Round? activeRound,
        CancellationToken cancellationToken = default);

    // REQ-407: participant-only, live, active-round-scoped leaderboard.
    // activeRound must be a real, already-resolved active round — callers
    // (the API layer) are responsible for returning a "no active round"
    // response themselves before ever calling this, mirroring RoundEndpoints'
    // existing REQ-303 pattern; this method has no null-round case to handle.
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
    Task<LeaderboardPage> GetWindowedLeaderboardAsync(
        Guid requestingUserId,
        string gameKey,
        LeaderboardWindowResolution resolution,
        DateTime nowUtc,
        int cursor,
        int pageSize,
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
// mid-list.
public record LeaderboardEntry(int Rank, Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

// RequestingUserEntry is populated whenever the requesting user appears
// anywhere in the ranked list — including when their row falls outside Rows
// for the current page — but is null when they don't appear in the ranked
// list at all. For GetGlobalLeaderboardAsync specifically (REQ-401/404,
// 2026-07-20), that now includes a requesting user who is a league member
// but has never submitted a single Guess: membership alone (every
// authenticated caller today, via signup auto-add, AuthController.cs) no
// longer guarantees a ranked row.
public record LeaderboardPage(
    IReadOnlyList<LeaderboardEntry> Rows,
    LeaderboardEntry? RequestingUserEntry,
    int? NextCursor,
    bool HasMore);

// REQ-408: one browsable closed round, for the round-selection list. Never
// carries the active/upcoming round (Round.ClosedAt is only ever set once
// RoundCloseService has actually closed it).
public record ClosedRoundSummary(Guid RoundId, DateTime StartTime, DateTime EndTime, DateTime ClosedAt);

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
