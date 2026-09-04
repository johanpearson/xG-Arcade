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

    // Bug fix/design change (2026-09-04, REQ-1406, product-owner direction):
    // supersedes the former HaveOverlapAtClubAsync(playerAId, playerBId,
    // clubName), which required the CALLER to already know and type the
    // specific club name — the exact source of a real false-rejection bug
    // (a player typing "Chelsea FC" against a stored, canonicalized
    // "Chelsea" — see ClubNameNormalizer's own doc comment for that
    // incident). The chain-builder no longer asks the player to name a
    // club at all: this returns every club (with its overlapping year
    // range) the two players actually share, so the caller can both decide
    // validity (empty = never played together) AND display the real
    // answer, never a player-typed string that has to match anything.
    // Same "fetch once, cache forever, throw LiveLookupUnavailableException
    // on a genuine technical failure" contract as HaveSharedClubOverlapAsync
    // above (both share the same underlying fetch).
    Task<IReadOnlyList<SharedClubOverlap>> GetSharedClubOverlapsAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default);
}

// One club both players share, with the overlapping window of their two
// stints there (not each player's own full stint — the intersection).
// OverlapEndYear is null only when BOTH players' stints at this club are
// still ongoing (both EndYear null) — mirrors PlayerCareerStint.EndYear's
// own "null = ongoing" convention. A pair of players who shared more than
// one club (e.g. Maxwell and Zlatan Ibrahimović — Inter, Barcelona, PSG)
// gets one entry per club here, not just the first found.
public record SharedClubOverlap(string ClubName, int OverlapStartYear, int? OverlapEndYear);
