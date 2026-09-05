using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// REQ-1412/1413/1414, ADR-0109: see IConnectChainStepDisputeService's own
// doc comment.
public class ConnectChainStepDisputeService(
    IConnectMatchRepository connectMatchRepository,
    IConnectMatchLifecycleService connectMatchLifecycleService,
    TimeProvider timeProvider) : IConnectChainStepDisputeService
{
    public async Task<RaiseChainStepDisputeResult> RaiseDisputeAsync(
        Guid matchId, Guid chainStepId, Guid userId, string claimedClubName, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.NotAParticipant, null);
        var match = access.Match!;

        if (string.IsNullOrWhiteSpace(claimedClubName))
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.InvalidClaimedClubName, null);

        var step = await connectMatchRepository.GetChainStepByIdAsync(chainStepId, cancellationToken);
        if (step is null || step.ConnectMatchId != matchId)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.StepNotFound, null);

        // REQ-1412: "a dispute can only be raised by the step's own owner."
        if (step.UserId != userId)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.NotStepOwner, null);

        if (step.IsValid)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.StepNotInvalid, null);

        var existingDispute = await connectMatchRepository.GetDisputeForChainStepAsync(chainStepId, cancellationToken);
        if (existingDispute is not null)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.AlreadyDisputed, null);

        // REQ-1412: "only on that player's own most-recent invalid step" —
        // the chain frontier only ever advances past a position via an
        // effectively-valid step (ConnectChainStepExtensions.
        // IsEffectivelyValid), so at most one position can ever have an
        // unresolved, undisputed invalid step at a time; comparing
        // AttemptNumber within this exact position is therefore sufficient
        // to detect "an old, superseded failure" (e.g. a failed first
        // attempt once the retry at that same position has also been
        // submitted, whether that retry passed or failed).
        var ownSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, userId, cancellationToken);
        var latestAttemptAtPosition = ownSteps.Where(s => s.Position == step.Position).Max(s => s.AttemptNumber);
        if (step.AttemptNumber != latestAttemptAtPosition)
            return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.StepSuperseded, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // REQ-1412's reopen rule: raising a dispute against a step whose
        // match has ALREADY resolved (the known, accepted consequence of a
        // bust firing synchronous resolution the instant the opponent is
        // also already terminal — see ConnectMatchLifecycleService.
        // ResolveIfBothTerminalAsync's own comment) reopens the match
        // BEFORE the new Pending dispute is persisted, so that method's own
        // gate sees an Active match with a genuine Pending dispute in it,
        // not a Resolved one it would otherwise ignore entirely (that
        // method's very first check is `if (match.Status == Resolved)
        // return false`). StartedAt/DeadlineUtc are untouched — REQ-1413's
        // "the deadline is not paused... by a pending dispute" rule.
        if (match.Status == ConnectMatchStatus.Resolved)
            await connectMatchRepository.ReopenMatchAsync(matchId, cancellationToken);

        var dispute = new ConnectChainStepDispute
        {
            Id = Guid.NewGuid(),
            ConnectChainStepId = chainStepId,
            ClaimedClubName = claimedClubName.Trim(),
            Status = ConnectChainStepDisputeStatus.Pending,
            RaisedAt = now,
        };
        var persisted = await connectMatchRepository.AddDisputeAsync(dispute, cancellationToken);

        // REQ-1412/1413 (product-owner confirmation, 2026-09-05): raising a
        // dispute — on EITHER the first or the bust-causing second failure
        // at this position — consumes that position's one REQ-1407 retry
        // the instant it's raised, so the caller's slot is marked busted
        // now, unconditionally. For a bust-causing (AttemptNumber >= 2)
        // dispute this is a harmless no-op (MarkPlayerBustedAsync's own
        // `??=` idempotency — SubmitChainStepAsync's bust branch already
        // marked this slot busted before the dispute even existed). For a
        // first-failure dispute this is the new, real effect: the player is
        // provisionally busted from this instant, reversed only if this
        // dispute is later Approved (ApproveDisputeAsync's own
        // step/dispute update, paired with ClearPlayerBustedAsync below).
        var callerIsPlayerA = match.PlayerAUserId == userId;
        await connectMatchRepository.MarkPlayerBustedAsync(matchId, callerIsPlayerA, now, cancellationToken);

        // Mirrors every other terminal-state-affecting write in this
        // component (SubmitChainStepAsync's own bust/chain-closed branches)
        // by attempting resolution immediately — but this dispute is now
        // Pending, so ResolveIfBothTerminalAsync's own REQ-1413 gate always
        // returns false from this call site; kept for consistency (and to
        // correctly resolve once every OTHER dispute, if any, is already
        // reviewed), not because it can succeed here today.
        await connectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync(matchId, cancellationToken);

        return new RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome.Raised, persisted);
    }

    public async Task<ReviewChainStepDisputeResult> ReviewDisputeAsync(
        Guid matchId, Guid disputeId, Guid reviewerUserId, bool approve, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, reviewerUserId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.NotAParticipant, null);
        var match = access.Match!;

        var dispute = await connectMatchRepository.GetDisputeByIdAsync(disputeId, cancellationToken);
        if (dispute is null)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.DisputeNotFound, null);

        var step = await connectMatchRepository.GetChainStepByIdAsync(dispute.ConnectChainStepId, cancellationToken);
        if (step is null || step.ConnectMatchId != matchId)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.DisputeNotFound, null);

        // REQ-1413: "only the other participant... never the disputing
        // player" — the reviewer IS a match participant (checked above), so
        // this is the narrower "not the same player who raised it" check.
        if (step.UserId == reviewerUserId)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.CannotReviewOwnDispute, null);

        if (dispute.Status != ConnectChainStepDisputeStatus.Pending)
            return new ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome.AlreadyReviewed, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var disputerIsPlayerA = match.PlayerAUserId == step.UserId;

        if (approve)
        {
            await connectMatchRepository.ApproveDisputeAsync(disputeId, now, cancellationToken);

            // REQ-1414: candidate + immediately preceding chain player, the
            // same "preceding player" concept SubmitChainStepAsync itself
            // computes — the most recently effectively-valid step BEFORE
            // this one in the disputing player's own chain, or (position 1)
            // that player's own fixed target pick.
            var precedingPlayerId = await ResolvePrecedingPlayerIdAsync(matchId, step, cancellationToken);

            await connectMatchRepository.AddDataCorrectionSuggestionAsync(new ConnectDisputeDataCorrectionSuggestion
            {
                Id = Guid.NewGuid(),
                ConnectChainStepDisputeId = disputeId,
                ConnectMatchId = matchId,
                ConnectChainStepId = step.Id,
                CandidatePlayerId = step.CandidatePlayerId,
                PrecedingPlayerId = precedingPlayerId,
                ClaimedClubName = dispute.ClaimedClubName,
                CreatedAt = now,
            }, cancellationToken);

            // REQ-1413: an approved dispute means this player did NOT
            // actually bust — reverses the provisional bust
            // RaiseDisputeAsync set unconditionally.
            await connectMatchRepository.ClearPlayerBustedAsync(matchId, disputerIsPlayerA, cancellationToken);
        }
        else
        {
            // REQ-1413: the provisional bust set at raise time is NOT
            // cleared — it stands as this position's real, final bust.
            await connectMatchRepository.DenyDisputeAsync(disputeId, now, cancellationToken);
        }

        // Same "attempt resolution immediately" pattern as every other
        // terminal-state-affecting write in this component — this review
        // may be the last outstanding Pending dispute in the match, in
        // which case REQ-1413's gate now lets resolution actually proceed.
        await connectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync(matchId, cancellationToken);

        return new ReviewChainStepDisputeResult(
            approve ? ReviewChainStepDisputeOutcome.Approved : ReviewChainStepDisputeOutcome.Denied, dispute);
    }

    public async Task<GetChainStepDisputesResult> GetDisputesForMatchAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new GetChainStepDisputesResult(GetChainStepDisputesOutcome.MatchNotFound, []);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new GetChainStepDisputesResult(GetChainStepDisputesOutcome.NotAParticipant, []);
        var match = access.Match!;

        var playerASteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerAUserId, cancellationToken);
        var playerBSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerBUserId, cancellationToken);

        // A plain loop rather than .ToDictionary(...) — REQ-710 anonymizes
        // ConnectMatch.PlayerAUserId/PlayerBUserId to null in place, and if
        // BOTH participants of an old match were ever anonymized, both
        // GetChainStepsForMatchAndUserAsync(matchId, null) calls above would
        // return the SAME full set of now-ownerless rows, which
        // .ToDictionary would throw on (duplicate keys) — this degrades
        // gracefully instead (last write wins, functionally a no-op since
        // it's the same row either way).
        var stepsById = new Dictionary<Guid, ConnectChainStep>();
        foreach (var s in playerASteps.Concat(playerBSteps))
            stepsById[s.Id] = s;

        var disputes = await connectMatchRepository.GetDisputesForChainStepsAsync(stepsById.Keys.ToList(), cancellationToken);

        var views = disputes
            .Select(d =>
            {
                var step = stepsById[d.ConnectChainStepId];
                return new ChainStepDisputeView(
                    d.Id, d.ConnectChainStepId, step.Position, d.ClaimedClubName, d.Status,
                    d.RaisedAt, d.ReviewedAt, step.UserId == userId);
            })
            .OrderBy(v => v.RaisedAt)
            .ToList();

        return new GetChainStepDisputesResult(GetChainStepDisputesOutcome.Found, views);
    }

    // REQ-1414: mirrors ConnectChainStepService.SubmitChainStepAsync's own
    // "immediately preceding player" computation exactly (this player's own
    // fixed target pick for the very first position, otherwise the most
    // recently effectively-valid step's candidate).
    private async Task<Guid> ResolvePrecedingPlayerIdAsync(Guid matchId, ConnectChainStep step, CancellationToken cancellationToken)
    {
        var ownSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, step.UserId, cancellationToken);
        var priorStep = ownSteps
            .Where(s => s.Position < step.Position && s.IsEffectivelyValid())
            .OrderByDescending(s => s.Position)
            .FirstOrDefault();
        if (priorStep is not null)
            return priorStep.CandidatePlayerId;

        var targetPick = await connectMatchRepository.GetTargetPickAsync(matchId, step.UserId, cancellationToken);
        return targetPick!.TargetPlayerId;
    }
}
