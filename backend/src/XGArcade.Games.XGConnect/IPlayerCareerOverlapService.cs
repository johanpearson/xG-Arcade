namespace XGArcade.Games.XGConnect;

// S-211/REQ-1404: a generic, player-ID-based "did these two players ever
// play for the same club at overlapping times" check — deliberately not
// shaped around ConnectTargetPick at all (its public signature takes two
// bare Guids), since S-213's chain-step validation (REQ-1406) needs the
// identical check applied to arbitrary player pairs along a chain, not just
// the two match-opening target picks. See docs/backlog.md's S-211 entry
// ("extract it as a shared helper/service, since S-213 needs the identical
// check per chain step") and ADR-0010/0011 for the underlying live-lookup
// pattern this reuses rather than inventing a new data path.
//
// Placed in Games.XGConnect (COMP-17), not Core — it depends directly on
// XGArcade.DataSync's IPlayerCareerStintRefreshService and XGArcade.Data's
// IPlayerCareerStintRepository, the same way a game-module-owned business-
// rule service is allowed to depend on a shared DataSync service: e.g.
// Games.XGPath's own PathEligibilityService injects IPlayerFamiliarityService,
// and XGPathGameModule itself injects this same IPlayerCareerStintRefreshService,
// both as already-built DataSync services rather than reimplementing
// DataSync logic themselves (PlayerFamiliarityService/
// PlayerCareerStintRefreshService themselves live in XGArcade.DataSync/
// Wikidata, not in Games.XGPath). There is no cross-player overlap check
// anywhere else in this codebase to reuse (confirmed by this session's own
// research) — this is new.
public interface IPlayerCareerOverlapService
{
    // Never returns false for "we don't know" — throws
    // XGArcade.Core.Games.LiveLookupUnavailableException instead whenever a
    // live Wikidata refresh was needed (see the implementation's own doc
    // comment for exactly when that is) and failed technically (timeout/
    // HTTP/parse error). The caller is responsible for turning that into a
    // genuinely-unknown outcome (never fail-closed as "not connected," never
    // fail-closed as "connected") — same ADR-0010/0011 discipline
    // GuessSubmissionService/GridLiveLookupDispatcher already apply to
    // REQ-211's guess-time fallback.
    Task<bool> HaveSharedClubOverlapAsync(Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default);
}
