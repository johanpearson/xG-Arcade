using XGArcade.Data.Entities;

namespace XGArcade.Core.Social;

// COMP-16 (Core.Social)/ADR-0103, S-210: REQ-1403's opt-in half only.
// Opting in is itself the consent — there is no accept/decline step and no
// rejection branch (unlike IFriendService/IChallengeService, which both
// reject on duplicate-pending state), so this interface has no outcome
// enum: a call always succeeds and always creates a new Waiting row, even
// if the same user already has one (see MatchmakingOptIn's own entity doc
// comment — no unique constraint prevents this; the pairing sweep is what
// guards against a user being double-booked into two matches from their
// own two rows, not this method).
//
// The pairing/expiry sweep itself is deliberately NOT part of this
// interface — it needs Games.XGConnect's IConnectMatchRepository to create
// the resulting ConnectMatch, and ADR-0103 forbids Core.Social taking a
// compile-time dependency on that. See
// XGArcade.Api.Social.MatchmakingSweepService for that orchestration.
public interface IMatchmakingService
{
    // REQ-1403: creates a new Waiting MatchmakingOptIn for userId, with
    // ExpiresAt set to OptedInAt + 12h (the pairing window).
    Task<MatchmakingOptIn> OptInAsync(Guid userId, CancellationToken cancellationToken = default);
}
