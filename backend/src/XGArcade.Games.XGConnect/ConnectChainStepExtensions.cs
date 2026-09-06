using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

public static class ConnectChainStepExtensions
{
    // REQ-1412/1413 (quality-architect review, 2026-09-05): "is this
    // player's own bust column a REAL, blocking forfeit, or a PROVISIONAL
    // one a Pending dispute is currently covering (REQ-1412's own "the
    // player's chain continues... exactly as if the disputed step had
    // ordinarily validated" rule)". A null bustedAt is never a forfeit at
    // all, regardless of the steps. Otherwise, the single step that
    // currently answers this is the player's OWN most-recently-submitted
    // step — highest Position, then highest AttemptNumber at that
    // position, i.e. the current chain frontier — never "any step anywhere
    // in this player's history" (that broader read was exactly this bug:
    // an old, still-unreviewed dispute at an earlier position incorrectly
    // exempting a brand new, completely undisputed bust at a later one). If
    // that frontier step is itself a genuine failure (IsValid == false)
    // with no Pending dispute attached, the bust is real. If it's covered
    // by a Pending dispute — or the frontier has since moved past it onto a
    // later, ordinarily-valid step (that later step now being the most
    // recent one) — it isn't.
    //
    // Extracted once this exact question needed answering identically in
    // three places (ConnectChainStepService.SubmitChainStepAsync's
    // AlreadyForfeited check, ConnectMatchLifecycleService.
    // GetMatchesAwaitingActionAsync, ConnectMatchQueryService.
    // GetMatchDetailAsync's terminal-state construction) — mirrors
    // HasClosedChain's own once-extracted-when-it-hit-N-copies precedent
    // immediately below. ConnectMatchLifecycleService.ResolveIfBothTerminalAsync
    // deliberately does NOT use this helper: it already returns early
    // whenever ANY Pending dispute exists anywhere in the match, so by the
    // time it reads PlayerABustedAt/PlayerBBustedAt directly, no Pending
    // dispute could possibly be covering either bust — the raw column is
    // already known-real there.
    public static bool IsReallyBusted(this IReadOnlyList<ConnectChainStep> stepsForOnePlayerInOneMatch, DateTime? bustedAt)
    {
        if (bustedAt is null)
            return false;

        var frontierStep = stepsForOnePlayerInOneMatch
            .OrderByDescending(s => s.Position)
            .ThenByDescending(s => s.AttemptNumber)
            .FirstOrDefault();

        // Defensive, not expected in production: MarkPlayerBustedAsync is
        // only ever called right after AddChainStepAsync persists the
        // failing step itself, so a non-null bustedAt should always have at
        // least one step behind it. No steps at all means nothing could
        // possibly be a Pending dispute covering this bust either way, so
        // treat it as genuinely busted rather than throwing.
        if (frontierStep is null)
            return true;

        return !frontierStep.IsValid && !frontierStep.HasPendingDispute;
    }

    // REQ-1412/1413/ADR-0109: "does this step count as valid right now" —
    // its own real IsValid flag, OR a still-Pending dispute (REQ-1412's own
    // "the disputed step is treated as provisionally valid" rule), OR an
    // Approved dispute (which, in practice, ApproveDisputeAsync already
    // promotes into a real IsValid=true row — the OR here is a harmless,
    // always-true-anyway safety net, not a second source of truth). A
    // Denied dispute deliberately falls back to the step's own IsValid
    // (false) — REQ-1413's "a denied dispute... discarded" rule. Reads the
    // denormalized ConnectChainStep.HasPendingDispute cache rather than a
    // live dispute lookup — see that column's own doc comment for why, and
    // for the single place (ConnectMatchRepository's dispute methods) that
    // keeps it in sync with ConnectChainStepDispute.Status.
    //
    // Centralizes the "IsValid OR provisionally-valid-via-a-Pending-
    // dispute" rule so it can't drift between ConnectChainStepService and
    // ConnectMatchLifecycleService, mirroring HasClosedChain's own
    // once-extracted-when-it-hit-N-copies precedent immediately below.
    public static bool IsEffectivelyValid(this ConnectChainStep step) =>
        step.IsValid || step.HasPendingDispute;

    // REQ-1406/1407/1408/S-214: "has this player closed their chain for
    // this match" — a single effectively-valid (IsEffectivelyValid),
    // chain-closing (ClosesChain) row anywhere in their steps. Shared by
    // every call site that needs this exact check (ConnectChainStepService's
    // own ChainAlreadyComplete precondition, and ConnectMatchLifecycleService's
    // RunForfeitSweepAsync/TryResolveMatchIfBothTerminalAsync) so the
    // predicate itself can't drift between them — extracted once it hit
    // four independent copies (coding-guidelines.md's "Code health budget",
    // ADR-0084). A disputed step's own ClosesChain is always false (it
    // failed ordinary validation, so REQ-1406's own chain-closing check
    // never ran for it — see IConnectChainStepDisputeService's own doc
    // comment on why disputing deliberately does not recompute it), so a
    // merely-Pending dispute can never, by itself, make this true — the
    // player must still submit a further, genuinely closing step, exactly
    // as REQ-1412's own "the player's chain continues... exactly as if the
    // disputed step had ordinarily validated" text describes.
    public static bool HasClosedChain(this IReadOnlyList<ConnectChainStep> stepsForOnePlayerInOneMatch) =>
        stepsForOnePlayerInOneMatch.Any(s => s.IsEffectivelyValid() && s.ClosesChain);
}
