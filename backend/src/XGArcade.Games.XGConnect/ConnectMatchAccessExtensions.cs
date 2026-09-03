using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// Quality-gate-driven extraction (no REQ — pure internal deduplication, no
// behavior change): "load this match, then confirm the caller is one of
// its two participants" was reimplemented identically at four call sites
// across three files (ConnectTargetPickService.SubmitTargetPickAsync,
// ConnectChainStepService.SubmitChainStepAsync, and both
// ConnectChatService methods) once S-215/REQ-1410 added the chat service's
// two copies on top of the two pre-existing ones — the same rule-of-three
// pattern ConnectChainStepExtensions.HasClosedChain() was extracted for in
// S-214 (coding-guidelines.md's "Code health budget", ADR-0084).
//
// Deliberately does NOT return one of the callers' own result records
// (SubmitTargetPickResult/SubmitChainStepResult/SendChatMessageResult/
// GetChatMessagesResult) — those four types are unrelated to each other
// and each caller needs to build its own on failure, so this only resolves
// "found + participant?" and leaves turning that into the caller-specific
// failure result to the caller.
public static class ConnectMatchAccessExtensions
{
    // Loads the match and checks the caller is PlayerA or PlayerB on it.
    // Callers switch on Outcome: MatchNotFound/NotAParticipant map directly
    // onto each caller's own outcome enum, and Match is non-null if and
    // only if Outcome is Found.
    public static async Task<ConnectMatchAccessResult> ResolveParticipantMatchAsync(
        this IConnectMatchRepository connectMatchRepository,
        Guid matchId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var match = await connectMatchRepository.GetMatchByIdAsync(matchId, cancellationToken);
        if (match is null)
            return new ConnectMatchAccessResult(null, ConnectMatchAccessOutcome.MatchNotFound);

        if (match.PlayerAUserId != userId && match.PlayerBUserId != userId)
            return new ConnectMatchAccessResult(null, ConnectMatchAccessOutcome.NotAParticipant);

        return new ConnectMatchAccessResult(match, ConnectMatchAccessOutcome.Found);
    }
}

public enum ConnectMatchAccessOutcome
{
    Found,
    MatchNotFound,
    NotAParticipant,
}

public readonly record struct ConnectMatchAccessResult(ConnectMatch? Match, ConnectMatchAccessOutcome Outcome);
