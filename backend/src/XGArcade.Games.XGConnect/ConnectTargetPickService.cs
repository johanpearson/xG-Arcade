using XGArcade.Core.Games;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// S-211/REQ-1404, S-212/REQ-1405: see IConnectTargetPickService's own doc
// comment. The check-before-persist ordering below is what makes "a
// rejected completing pick is truly never written" trivially true — the
// caller's own row is never touched until the overlap check has already
// returned false. The completing-pick branch also triggers
// IConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync (REQ-1405)
// once both picks are locked — see that call's own inline comment for why
// this class doesn't compute the match-start transition itself.
public class ConnectTargetPickService(
    IConnectMatchRepository connectMatchRepository,
    IPlayerCareerOverlapService playerCareerOverlapService,
    IPlayerRepository playerRepository,
    IConnectMatchLifecycleService connectMatchLifecycleService,
    TimeProvider timeProvider) : IConnectTargetPickService
{
    public async Task<SubmitTargetPickResult> SubmitTargetPickAsync(
        Guid matchId, Guid userId, string targetPlayerName, string? targetWikidataQid = null,
        CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.NotAParticipant, null);
        var match = access.Match!;

        var callerExistingPick = await connectMatchRepository.GetTargetPickAsync(matchId, userId, cancellationToken);
        if (callerExistingPick is { IsLocked: true })
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.AlreadyLocked, null);

        // Bug fix (S-218 prep, ADR-0007; 2026-09-05, ADR-0107): resolve the
        // client-supplied NAME (or, when supplied, an unambiguous
        // WikidataQid) to a real Player.Id via the same
        // ConnectCandidateResolver ConnectChainStepService.SubmitChainStepAsync
        // uses — never trust a client-supplied id directly, since the only
        // search UI available to a client (`/players/autocomplete`,
        // COMP-10) returns ids from a different, unreconciled id space
        // (PlayerNameIndex.PlayerId). Checked before the otherExistingPick
        // branch below runs any live overlap lookup, so a target that
        // resolves to nothing never triggers one. See that resolver's own
        // doc comment for the real, reported same-name-collision incident
        // (two different real "Jonas Olsson"s) supplying a QID closes.
        var resolved = await ConnectCandidateResolver.ResolveAsync(
            playerRepository, targetPlayerName, targetWikidataQid, cancellationToken);
        if (resolved.Outcome == ConnectCandidateResolver.Outcome.NotFound)
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.TargetPlayerNotFound, null);
        var targetPlayerId = resolved.PlayerId;

        var otherUserId = match.PlayerAUserId == userId ? match.PlayerBUserId : match.PlayerAUserId;
        var otherExistingPick = otherUserId is null
            ? null
            : await connectMatchRepository.GetTargetPickAsync(matchId, otherUserId, cancellationToken);

        var selectedAt = timeProvider.GetUtcNow().UtcDateTime;

        // REQ-1404: the other participant has no pick yet — this selection
        // is recorded for the caller only, doesn't constrain the other
        // player's own independent selection, and is freely resubmittable
        // (AddOrUpdateTargetPickAsync stores-or-replaces). No overlap check
        // runs — there's nothing yet to compare against.
        if (otherExistingPick is null)
        {
            var stored = await connectMatchRepository.AddOrUpdateTargetPickAsync(
                matchId, userId, targetPlayerId, selectedAt, cancellationToken);
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.RecordedAwaitingOther, stored);
        }

        // This submission would complete the pair — check BEFORE writing
        // anything, so a rejection never touches the caller's own row.
        bool overlaps;
        try
        {
            overlaps = await playerCareerOverlapService.HaveSharedClubOverlapAsync(
                targetPlayerId, otherExistingPick.TargetPlayerId, cancellationToken);
        }
        catch (LiveLookupUnavailableException)
        {
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.LiveLookupUnavailable, null);
        }

        if (overlaps)
            return new SubmitTargetPickResult(SubmitTargetPickOutcome.TriviallyConnected, null);

        var lockedPick = await connectMatchRepository.AddOrUpdateTargetPickAsync(
            matchId, userId, targetPlayerId, selectedAt, cancellationToken);
        await connectMatchRepository.LockTargetPicksForMatchAsync(matchId, cancellationToken);

        // REQ-1405/S-212: both target picks are now locked — start the
        // shared 6h forfeit clock. Delegated to
        // IConnectMatchLifecycleService rather than computed inline here so
        // this class stays scoped to target-pick selection only (REQ-1404);
        // that service independently re-confirms "both picks locked" rather
        // than trusting this call site blindly.
        await connectMatchLifecycleService.StartMatchIfBothPicksLockedAsync(matchId, cancellationToken);

        // The repository call above is the persisted source of truth for
        // both rows' IsLocked flag — this local mutation just keeps the
        // object this method returns in sync with it for the caller,
        // without relying on EF Core's tracked-instance identity-map
        // behavior to do that implicitly.
        lockedPick.IsLocked = true;

        return new SubmitTargetPickResult(SubmitTargetPickOutcome.RecordedAndLocked, lockedPick);
    }
}
