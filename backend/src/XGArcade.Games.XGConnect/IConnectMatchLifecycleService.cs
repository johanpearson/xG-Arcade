namespace XGArcade.Games.XGConnect;

// REQ-1405/S-212, REQ-1407/1408/1409/S-214: match-start, 6h forfeit-timer,
// bust/completion-aware resolution scaffolding — layered on top of
// IConnectMatchRepository's persistence primitives (S-208's additions) and
// IConnectScoringService (S-214), same "outcome enum + result record"-free
// but still service-owns-the-transition shape as IConnectTargetPickService.
// Lives in Games.XGConnect (COMP-17), not XGArcade.Api, because — unlike
// MatchmakingSweepService (XGArcade.Api.Social) — neither method here
// orchestrates anything outside this component's own
// IConnectMatchRepository; there is no Core.Social state to cross-write.
public interface IConnectMatchLifecycleService
{
    // Called right after IConnectMatchRepository.LockTargetPicksForMatchAsync
    // (ConnectTargetPickService's completing-pick branch) — but deliberately
    // re-checks state itself via GetTargetPicksForMatchAsync rather than
    // trusting the caller blindly. No-ops (does not throw, does not touch
    // ConnectMatch) unless there are exactly two ConnectTargetPick rows for
    // this match and both are IsLocked.
    Task StartMatchIfBothPicksLockedAsync(Guid matchId, CancellationToken cancellationToken = default);

    // The periodic forfeit sweep (REQ-1405) — see ForfeitSweepResult's own
    // doc comment for what each count means and
    // ConnectMatchLifecycleService.RunForfeitSweepAsync's own doc comment
    // for why a same-pass resolution is the mechanism that satisfies
    // "resolves immediately once both players are terminal, never waiting
    // out an unused remainder of the window."
    Task<ForfeitSweepResult> RunForfeitSweepAsync(CancellationToken cancellationToken = default);

    // REQ-1409/S-214: the single place every one of a match's three
    // terminal-reaching paths (timeout/REQ-1405, bust/REQ-1407, chain
    // completion/REQ-1408) converges — re-evaluates this match's own
    // terminal state from scratch every time it's called (rather than
    // trusting the caller's own view of "did this just make both players
    // terminal"), so it is safe to call from multiple call sites without
    // coordination: ConnectChainStepService.SubmitChainStepAsync (right
    // after persisting a ChainClosed step or marking a bust) and
    // RunForfeitSweepAsync above (once per swept match, after marking
    // whichever slots are newly timed out). No-ops (returns false, touches
    // nothing) if the match is already Resolved or if either player has not
    // yet reached a terminal state. Returns true only when this call is the
    // one that actually resolved the match.
    Task<bool> TryResolveMatchIfBothTerminalAsync(Guid matchId, CancellationToken cancellationToken = default);
}

// PlayersForfeited: how many individual player SLOTS were newly marked
// timed-out this sweep call (0, 1, or 2 per match swept, summed across every
// match past its deadline). MatchesResolved: how many matches transitioned
// to Resolved this same call — always a match whose both slots reached
// terminal (by timeout, the only terminal-reaching path this story wires
// up) within this one pass, per REQ-1405's "resolves immediately, never
// deferred to a later pass" rule.
public record ForfeitSweepResult(int PlayersForfeited, int MatchesResolved);
