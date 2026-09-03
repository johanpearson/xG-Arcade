using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// S-213/REQ-1406, S-214/REQ-1407: see IConnectChainStepService's own doc
// comment. The check-before-persist ordering mirrors ConnectTargetPickService's
// own doc comment — every live-overlap check this method runs is fully
// resolved before any write, with the one deliberate exception that a step
// failing its main check IS persisted (that failure IS the outcome this
// entity exists to record; see ConnectChainStep's own doc comment).
public class ConnectChainStepService(
    IConnectMatchRepository connectMatchRepository,
    IPlayerCareerOverlapService playerCareerOverlapService,
    IPlayerRepository playerRepository,
    IConnectMatchLifecycleService connectMatchLifecycleService,
    TimeProvider timeProvider) : IConnectChainStepService
{
    public async Task<SubmitChainStepResult> SubmitChainStepAsync(
        Guid matchId, Guid userId, string candidatePlayerName, string claimedClubName,
        CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new SubmitChainStepResult(SubmitChainStepOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new SubmitChainStepResult(SubmitChainStepOutcome.NotAParticipant, null);
        var match = access.Match!;

        if (match.Status != ConnectMatchStatus.Active)
            return new SubmitChainStepResult(SubmitChainStepOutcome.MatchNotActive, null);

        // REQ-1407: the caller's own slot may already be terminal (busted
        // or timed out) even while ConnectMatch.Status is still Active,
        // because Status only flips to Resolved once BOTH players are
        // terminal (REQ-1405/1409) — a player who already forfeited must
        // not be able to submit further steps just because their opponent
        // hasn't finished yet. This closes a gap the MatchNotActive check
        // above does NOT cover on its own.
        var callerIsPlayerA = match.PlayerAUserId == userId;
        var callerAlreadyForfeited = callerIsPlayerA
            ? match.PlayerABustedAt is not null || match.PlayerATimedOutAt is not null
            : match.PlayerBBustedAt is not null || match.PlayerBTimedOutAt is not null;
        if (callerAlreadyForfeited)
            return new SubmitChainStepResult(SubmitChainStepOutcome.AlreadyForfeited, null);

        var existingSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, userId, cancellationToken);
        if (existingSteps.HasClosedChain())
            return new SubmitChainStepResult(SubmitChainStepOutcome.ChainAlreadyComplete, null);

        var validSteps = existingSteps.Where(s => s.IsValid).ToList();

        // REQ-1406: the "immediately preceding player in the chain" — the
        // most recently accepted valid step's candidate, or, for the very
        // first step, the caller's OWN fixed target pick (every player's
        // chain starts from their own target pick, per REQ-1406's own Given
        // clause). The caller's target pick is guaranteed to exist here —
        // ConnectMatch.Status can only be Active once both target picks are
        // locked (REQ-1405) — so no defensive null-handling for a state
        // that can't happen (CLAUDE.md/coding-guidelines.md).
        Guid precedingPlayerId;
        if (validSteps.Count > 0)
        {
            precedingPlayerId = validSteps.OrderByDescending(s => s.Position).First().CandidatePlayerId;
        }
        else
        {
            var callerTargetPick = await connectMatchRepository.GetTargetPickAsync(matchId, userId, cancellationToken);
            precedingPlayerId = callerTargetPick!.TargetPlayerId;
        }

        var nextPosition = (validSteps.Count > 0 ? validSteps.Max(s => s.Position) : 0) + 1;
        var attemptNumber = existingSteps.Count(s => s.Position == nextPosition) + 1;

        var normalizedName = PlayerNameNormalizer.Normalize(candidatePlayerName);
        var candidates = await playerRepository.GetPlayersByNormalizedFullNameAsync(normalizedName, cancellationToken);
        if (candidates.Count == 0)
            return new SubmitChainStepResult(SubmitChainStepOutcome.CandidateNotFound, null);

        // No client-supplied disambiguation id exists for this endpoint,
        // unlike xG Grid's REQ-209 — deliberately pick deterministically
        // (lowest Id) rather than inventing a new disambiguation mechanism
        // for this story. A known, deliberate simplification, not a new
        // REQ: a same-name collision here just means the live overlap check
        // below runs against whichever of the same-named players sorts
        // first, which may occasionally reject a claim that would have
        // validated against the OTHER same-named player.
        var candidate = candidates.OrderBy(p => p.Id).First();

        var submittedAt = timeProvider.GetUtcNow().UtcDateTime;

        bool overlapsAtClaimedClub;
        try
        {
            overlapsAtClaimedClub = await playerCareerOverlapService.HaveOverlapAtClubAsync(
                candidate.Id, precedingPlayerId, claimedClubName, cancellationToken);
        }
        catch (LiveLookupUnavailableException)
        {
            return new SubmitChainStepResult(SubmitChainStepOutcome.LiveLookupUnavailable, null);
        }

        if (!overlapsAtClaimedClub)
        {
            var invalidStep = new ConnectChainStep
            {
                Id = Guid.NewGuid(),
                ConnectMatchId = matchId,
                UserId = userId,
                Position = nextPosition,
                AttemptNumber = attemptNumber,
                CandidatePlayerId = candidate.Id,
                ClaimedClubName = claimedClubName,
                IsValid = false,
                ClosesChain = false,
                SubmittedAt = submittedAt,
            };
            var persistedInvalidStep = await connectMatchRepository.AddChainStepAsync(invalidStep, cancellationToken);

            // REQ-1407: a first-attempt failure (AttemptNumber == 1) just
            // allows a retry — no further action here, the +1 penalty this
            // incurs is derived later at scoring time (IConnectScoringService)
            // by counting invalid first attempts, never stored as a running
            // counter. A second, consecutive failure at the SAME position
            // (AttemptNumber == 2, the one allowed retry) busts the player —
            // their slot is marked terminal and match resolution is
            // attempted immediately, since they just reached a terminal
            // state.
            if (attemptNumber >= 2)
            {
                await connectMatchRepository.MarkPlayerBustedAsync(matchId, callerIsPlayerA, submittedAt, cancellationToken);
                await connectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync(matchId, cancellationToken);
                return new SubmitChainStepResult(SubmitChainStepOutcome.Busted, persistedInvalidStep);
            }

            return new SubmitChainStepResult(SubmitChainStepOutcome.InvalidStep, persistedInvalidStep);
        }

        // REQ-1406: chain-closing is checked against the OTHER participant's
        // target pick — never the one this chain started from — and against
        // ANY shared overlapping club (the existing, unmodified
        // HaveSharedClubOverlapAsync), not restricted to this step's own
        // claimedClubName.
        var otherUserId = match.PlayerAUserId == userId ? match.PlayerBUserId : match.PlayerAUserId;
        var otherTargetPick = await connectMatchRepository.GetTargetPickAsync(matchId, otherUserId, cancellationToken);

        bool closesChain;
        try
        {
            closesChain = await playerCareerOverlapService.HaveSharedClubOverlapAsync(
                candidate.Id, otherTargetPick!.TargetPlayerId, cancellationToken);
        }
        catch (LiveLookupUnavailableException)
        {
            // The whole step — including its already-passed main check — is
            // discarded, never partially persisted: REQ-1406 requires the
            // closing determination to be resolved before the step counts
            // as accepted at all.
            return new SubmitChainStepResult(SubmitChainStepOutcome.LiveLookupUnavailable, null);
        }

        var acceptedStep = new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = nextPosition,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = candidate.Id,
            ClaimedClubName = claimedClubName,
            IsValid = true,
            ClosesChain = closesChain,
            SubmittedAt = submittedAt,
        };
        var persistedAcceptedStep = await connectMatchRepository.AddChainStepAsync(acceptedStep, cancellationToken);

        // REQ-1408/1409: a closed chain is this player's own terminal
        // state — attempt resolution immediately, since the caller may just
        // have completed the second (or only remaining) side of this match.
        if (closesChain)
            await connectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync(matchId, cancellationToken);

        return new SubmitChainStepResult(
            closesChain ? SubmitChainStepOutcome.ChainClosed : SubmitChainStepOutcome.StepAccepted,
            persistedAcceptedStep);
    }
}
