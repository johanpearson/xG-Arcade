using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// S-213/REQ-1406 (docs/requirements-document.md §4.15): incremental
// connection-chain step submission and live validation, layered on top of
// IConnectMatchRepository's pure persistence primitives (S-208) — same
// "outcome enum + result record for expected branches" shape as
// IConnectTargetPickService/ISubmitTargetPickResult (S-211).
public interface IConnectChainStepService
{
    // Check-before-persist, same discipline as
    // ConnectTargetPickService.SubmitTargetPickAsync: every live-overlap
    // check (both the main claimed-club check and, when it succeeds, the
    // chain-closing check against the other target pick) is fully resolved
    // BEFORE anything is written, except for the deliberate exception that a
    // step that fails its main check IS persisted (InvalidStep) — that
    // failure outcome is itself the thing this entity exists to record (see
    // ConnectChainStep's own doc comment).
    Task<SubmitChainStepResult> SubmitChainStepAsync(
        Guid matchId, Guid userId, string candidatePlayerName, string claimedClubName,
        CancellationToken cancellationToken = default);
}

public enum SubmitChainStepOutcome
{
    // REQ-1406: the claimed overlap checked out, and the candidate does NOT
    // also close the chain — appended as the chain's next link, and the
    // player may submit another step or attempt to close the chain.
    StepAccepted,

    // REQ-1406: the claimed overlap checked out AND the candidate closes the
    // chain (a valid, overlapping-time shared-club connection to the
    // match's OTHER target pick — never the one the chain started from).
    // The player's chain is complete; no further steps may be submitted by
    // them for this match.
    ChainClosed,

    // REQ-1406: the claimed candidate/club overlap did NOT check out — the
    // step is still persisted (IsValid = false, ClosesChain = false) since
    // recording the outcome of every attempt is this entity's whole
    // purpose (feeds S-214's future strike-counting). This story does not
    // enforce any cap on how many invalid attempts may be made at a
    // position — that is S-214's job.
    InvalidStep,

    MatchNotFound,

    // The caller is neither PlayerAUserId nor PlayerBUserId on this match.
    NotAParticipant,

    // REQ-1406's "given an active match" precondition — the match hasn't
    // started yet (target picks not both locked) or has already resolved.
    MatchNotActive,

    // REQ-1406: this player already has a step with IsValid && ClosesChain
    // for this match — "no further steps may be submitted by that player
    // for this match."
    ChainAlreadyComplete,

    // REQ-1407/S-214: the claimed overlap did NOT check out on the caller's
    // one allowed retry (AttemptNumber 2) at this position — the second,
    // consecutive failure at the same position. The step IS persisted
    // (IsValid = false, same as InvalidStep) but the caller's slot is also
    // marked busted (a terminal state) and match resolution is attempted —
    // distinct from InvalidStep so a caller/test can tell an ordinary
    // first-attempt failure apart from one that just ended the match for
    // this player.
    Busted,

    // REQ-1407/S-214: the caller's own slot on this match already reached a
    // terminal state (busted or timed out) before this submission — no
    // further steps may be submitted by that player, even while
    // ConnectMatch.Status is still Active (true whenever the OTHER player
    // hasn't yet reached terminal). Nothing is persisted.
    AlreadyForfeited,

    // REQ-1406/ADR-0007: candidatePlayerName didn't resolve to any known
    // Player (COMP-06) via an exact normalized-name match. Nothing is
    // persisted — ConnectChainStep.CandidatePlayerId is a required,
    // non-nullable FK, so there is no real player id to store.
    CandidateNotFound,

    // ADR-0010/0011: a live Wikidata refresh (either the main claimed-club
    // check or the chain-closing check) didn't complete in time — genuinely
    // unknown, not a rejection of anything the player did. Nothing is
    // persisted, mirroring ConnectTargetPickService's own
    // LiveLookupUnavailable discipline (including the chain-closing check:
    // if THAT check fails after the main check already passed, the whole
    // step — main check included — is discarded, never partially
    // persisted).
    LiveLookupUnavailable,
}

// ChainStep is non-null only for StepAccepted/ChainClosed/InvalidStep/Busted
// — the four outcomes that actually persist a row (Busted's row IS the
// InvalidStep-shaped row that triggered the bust). Null for every outcome
// where nothing was (or could be) written.
public record SubmitChainStepResult(SubmitChainStepOutcome Outcome, ConnectChainStep? ChainStep);
