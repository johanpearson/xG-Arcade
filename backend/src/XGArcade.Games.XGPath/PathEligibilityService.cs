using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGPath;

// S-154 (pure refactor, no behavior change, docs/backlog.md Epic 17): split
// out of XGPathGameModule — owns REQ-1201's whole target-player eligibility
// pipeline: candidate narrowing (the perf-fix narrowing pass below),
// national-team/B-team stint sanitization and adjacent-same-club collapse
// (PathCareerStintFilter), the three IsEligible structural checks, the
// BirthYear/Position Player-level floors (ADR-0073/ADR-0079), and ADR-0056's
// familiarity filter. See GetEligiblePlayerIdsAsync's own doc comment for
// the full eligibility history (ADR-0047/ADR-0056/ADR-0073/ADR-0074/
// ADR-0075/ADR-0079/ADR-0081) — unchanged by this move, since none of that
// reasoning depended on which class hosted the code.
public class PathEligibilityService(
    // S-106/S-107 (pure refactor): the sibling repositories carrying the
    // methods split out of the original, now-deleted IPlayerStoreRepository
    // — see ADR-0067. playerCareerStintRepository carries
    // GetCareerStintCandidatePlayerIdsAsync/GetCareerStintsByPlayerIdsAsync.
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerFamiliarityService playerFamiliarityService) : IPathEligibilityService
{
    // REQ-1201/ADR-0047: a seeded-club stint only counts toward eligibility
    // if it reflects meaningful playing time there, not a one-off loan/
    // fringe appearance — see the ADR for why 20 and why an unknown count
    // still passes rather than being rejected.
    private const int MinAppearancesAtSeededClub = 20;

    // REQ-1201/ADR-0074/S-138: eligibility now requires ≥2 distinct
    // QUALIFYING seeded clubs, not just 1 (ADR-0047's old threshold). Two
    // rows at the SAME seeded club (a loan, then a later permanent return)
    // count as one qualifying club, not two — see IsEligible's own comment
    // for the exact "distinct club NAMES, not stint rows" semantics this
    // constant enforces. Named here, not a bare literal, because the
    // narrowing pass below (GetEligiblePlayerIdsAsync's
    // GetCareerStintCandidatePlayerIdsAsync call) needs the exact same
    // threshold IsEligible itself uses — one named constant, not two
    // independent magic 2s that could drift apart.
    private const int MinQualifyingSeededClubs = 2;

    // REQ-1203/ADR-0074/S-138 (architecture-review finding, not the
    // original backlog text): a total-stint-row floor is STILL required
    // here, independent of MinQualifyingSeededClubs above — it is NOT the
    // same check ADR-0045's original "≥3 distinct documented career club
    // stints" reasoning established, and is deliberately NOT dropped as
    // "redundant" the way the backlog story assumed. Reason:
    // PathClueSequenceBuilder.SplitIntoTurns divides a target's full stint
    // count N across exactly 3 fixed club-reveal turns and assumes N >= 3
    // (REQ-1203) — for N=2 it produces turn sizes [0, 1, 1], silently
    // showing the player ZERO clubs on the first club-reveal turn. Since
    // MinQualifyingSeededClubs (2) only bounds the number of QUALIFYING
    // SEEDED stints, not a candidate's TOTAL documented stint count, a
    // real player with exactly 2 total stints (both at qualifying seeded
    // clubs, no third unseeded stint) would otherwise pass eligibility and
    // break REQ-1203's turn split. This floor and MinQualifyingSeededClubs
    // are two independent, both-required conditions — see IsEligible's own
    // comment.
    private const int MinDocumentedStintCount = 3;

    // REQ-1201/ADR-0073/S-137: xG Path's own, additive floor — deliberately
    // separate from, and narrower than, REQ-112's shared 1939 pool floor
    // (enforced far upstream at Wikidata SPARQL query time, WikidataClient's
    // BuildPlayerPoolBirthYearQuery/ADR-0025, shared with xG Grid). Living
    // here as a Player-level check rather than inside PathCareerStintFilter
    // is deliberate: BirthYear is a fact about the PLAYER (Player.BirthYear,
    // REQ-1207), not about any individual PlayerCareerStint row, so it has
    // no natural home in a stint-level filter. See ADR-0073 for why this
    // isn't instead a shared SPARQL-level change (would also narrow xG
    // Grid's pool, out of scope — same reasoning Epic 12's intro in
    // docs/backlog.md already gives for why the 1939 floor couldn't simply
    // be raised in place).
    //
    // REQ-1201/ADR-0079/S-161: a second, independent Player-level floor sits
    // alongside this one in GetEligiblePlayerIdsAsync (not folded into this
    // constant, since it has no numeric threshold of its own) — a candidate
    // whose Player.Position is null/empty is excluded the same fail-closed
    // way a null BirthYear is. Player.Position staying null forever for a
    // subset of rows is deliberate, documented REQ-1207 behavior (a
    // Wikidata data gap, not a bug), but nothing previously stopped a
    // Position-less candidate from being SELECTED as a target, which let a
    // preventable "Position: not available" surface on a real puzzle screen
    // (the 2026-08-18 QA report ADR-0079 fixes). See the Position check's
    // own comment in GetEligiblePlayerIdsAsync and ADR-0079.
    private const int MinBirthYear = 1975;

    // REQ-1201: candidate eligibility. Reads only PlayerCareerStint (via
    // IPlayerStoreRepository, boundary rule 1 — Games.XGPath never touches
    // XGArcadeDbContext directly) and ClubDefinition (via
    // ICategoryValueRepository, the same call GridGameModule already makes
    // for REQ-109's club reference data) — never PlayerAttribute/
    // PlayerOverride, which remain xG Grid's own correctness-checking path
    // only (ADR-0042/PlayerCareerStint's own doc comment: "xG Grid's
    // correctness-checking path must NEVER read this table").
    //
    // REQ-112 pool membership (male, born 1939 or later) is deliberately
    // NOT re-checked here: Player has no Gender field at all, and while
    // Player.BirthYear now exists (REQ-1207/S-082, for xG Path's own
    // age/birth-year clue, not for pool filtering), re-deriving REQ-112's
    // pool membership from it here would duplicate a check that's already
    // structurally guaranteed — the restriction is enforced entirely
    // upstream, at Wikidata-query time (WikidataClient's
    // BuildCountryClubIntersectionQuery/BuildClubClubIntersectionQuery/
    // BuildPlayerPoolBirthYearQuery, all filtering on P21/P569 before
    // anything is ever persisted as a Player row — ADR-0025). Every
    // Player/PlayerCareerStint row already satisfies REQ-112 by
    // construction, the same reasoning GridGameModule itself relies on for
    // not re-checking this at runtime either.
    //
    // REQ-1201/ADR-0073/S-137: BirthYear >= MinBirthYear (1975) IS checked
    // here, below — this is NOT a re-check of REQ-112. It's a separate,
    // narrower, xG-Path-only floor with no upstream enforcement anywhere
    // (unlike REQ-112, nothing filters this at Wikidata-query time), so
    // unlike REQ-112 above it cannot be treated as "already guaranteed by
    // construction." See MinBirthYear's own comment and ADR-0073.
    //
    // REQ-1201/ADR-0079/S-161: Position != null/empty IS ALSO checked here,
    // below, in the same pass as BirthYear above — a second, independent
    // Player-level floor, not a re-check of BirthYear and not folded into
    // it. See MinBirthYear's neighboring comment above and ADR-0079.
    public async Task<IReadOnlyList<Guid>> GetEligiblePlayerIdsAsync(CancellationToken cancellationToken = default)
    {
        var seededClubNames = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => c.Name)
            .ToHashSet();

        // Perf fix (NOTES.md 2026-08-03): PlayerCareerStint has grown to
        // ~608K rows (ADR-0055's prefetch-player-careers job) and keeps
        // growing as more countries are added, so a full
        // GetAllCareerStintsByPlayerAsync-style read on every round
        // generation no longer scales. Narrow to real candidates first with
        // a cheap (PlayerId, ClubName)-only read — "at least
        // MinDocumentedStintCount (3) total rows AND at least
        // MinQualifyingSeededClubs (2) distinct seeded club names among a
        // player's stints" (ignoring the appearance-count sub-condition,
        // which only narrows further, since that projection doesn't carry
        // AppearanceCount) is computable from that projection alone and is
        // a true superset of IsEligible's actual candidates — it never
        // excludes one IsEligible would have accepted (see
        // GetCareerStintCandidatePlayerIdsAsync's own doc comment, REQ-1201/
        // REQ-1203/ADR-0074/S-138) — then load full stint data (all
        // columns, needed for the date-order and per-club appearance-count
        // checks) only for that narrowed set.
        var candidateIds = await playerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync(
            seededClubNames, MinDocumentedStintCount, MinQualifyingSeededClubs, cancellationToken);
        var stintsByPlayer = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(candidateIds, cancellationToken);

        // Bug fix (2026-08-08, REQ-1203): leftover pre-2026-08-02
        // youth-national-team rows (see PathCareerStintFilter's own doc
        // comment) are excluded here too, not just at the display path.
        // A junk row is never itself a club present in seededClubNames, so
        // (post-S-138) it can't directly manufacture a qualifying club that
        // wasn't real — but it still carries its own (StartYear, EndYear),
        // and an unfiltered junk row could coincidentally collide with a
        // real stint's date pair and cause IsEligible's order-determinable
        // check to spuriously fail a genuinely eligible candidate.
        // GetCareerStintCandidatePlayerIdsAsync's own raw-row narrowing
        // pass above is deliberately left unfiltered — it's documented as
        // a true, over-inclusive SUPERSET of IsEligible's real candidates
        // (a candidate it lets through but IsEligible then rejects is
        // exactly the intended, safe shape of that narrowing pass; it
        // would only be a bug if it excluded a genuinely eligible
        // candidate, which not filtering here never does).
        // S-139 (2026-08-18, REQ-1203/ADR-0075): ExcludeBTeams is chained
        // alongside ExcludeNationalTeams for the same reason — a leftover
        // B-team/reserve-team row (e.g. "Real Madrid Castilla") is not
        // itself a seeded club either, but can still collide on dates the
        // same way a national-team row can.
        // INVARIANT (S-139 fast-follow hardening, 2026-08-18/REQ-1203;
        // extended S-162/ADR-0081, 2026-08-19): the order of operations here
        // — fetch raw stints, THEN sanitize via
        // PathCareerStintFilter.ExcludeBTeams(ExcludeNationalTeams(...)),
        // THEN COLLAPSE adjacent same-club rows via
        // PathCareerStintFilter.CollapseAdjacentSameClub(...), THEN check
        // IsEligible — must NEVER change, and must NEVER be computed against
        // unsanitized/uncollapsed stint data. This is what guarantees
        // "always PuzzleCount puzzles per round, never an empty club-reveal
        // turn" as a structural property of every puzzle this method ever
        // selects a target for, not a display-time patch: IsEligible's own
        // MinDocumentedStintCount floor (>= 3) exists specifically so
        // PathClueSequenceBuilder.SplitIntoTurns always has >= 3 stints to
        // split across its 3 fixed club-reveal turns, and that guarantee
        // only holds if "eligible" is judged AFTER the same national-team/
        // B-team rows AND the same adjacent-same-club collapse
        // PathEndpoints.cs applies before ever building the clue sequence
        // are already reflected in the count — never before. GET
        // /path/current applies the identical three-deep filter chain
        // (CollapseAdjacentSameClub(ExcludeBTeams(ExcludeNationalTeams(...))))
        // to the same persisted stints, so its view can never diverge from
        // what this method already verified. Any future refactor of this
        // method must preserve this fetch->sanitize->collapse->eligible-check
        // ordering exactly — reordering it (or checking IsEligible before
        // sanitizing/collapsing) silently reopens the exact "empty clue" bug
        // class this invariant exists to close (see ADR-0074 for the
        // original bug this pattern fixed, and ADR-0081 for why collapse
        // joins the chain the same way the two Exclude filters did).
        //
        // CollapseAdjacentSameClub also requires its input sorted ascending
        // by chronological order (its own doc comment) — kvp.Value's row
        // order is not otherwise guaranteed by
        // GetCareerStintsByPlayerIdsAsync (no ORDER BY in that query), so an
        // explicit OrderBy(SequenceOrder) is inserted here, after the two
        // Excludes and before Collapse, mirroring PathEndpoints.cs's own
        // pre-existing OrderBy at the equivalent position in its chain.
        //
        // A documented, INTENTIONAL side effect of collapsing before
        // IsEligible's seeded-club appearance-count check
        // (MinAppearancesAtSeededClub, ADR-0047): a player whose true
        // single-club appearance total was split across two adjacent
        // sub-threshold rows (e.g. 15 + 15 = 30) now correctly counts as
        // qualifying, where before the split understated it. This is a GOOD
        // consequence of merging consistently, not a bug to guard against —
        // see ADR-0081's Consequences section.
        var structurallyEligibleIds = stintsByPlayer
            .Where(kvp => IsEligible(
                PathCareerStintFilter.CollapseAdjacentSameClub(
                    PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(kvp.Value))
                        .OrderBy(s => s.SequenceOrder)
                        .ToList()),
                seededClubNames))
            .Select(kvp => kvp.Key)
            .ToList();

        // REQ-1201/ADR-0073/S-137: BirthYear >= MinBirthYear (1975),
        // applied here rather than inside IsEligible/PathCareerStintFilter
        // because it's a fact about the PLAYER (Player.BirthYear), not
        // about any individual PlayerCareerStint row — stints have no
        // BirthYear of their own. Runs against exactly the structurally-
        // eligible set computed above, before familiarity filtering below,
        // mirroring ADR-0056's own "familiarity filter only sees
        // structurally-eligible candidates" ordering — no point spending a
        // familiarity-check call on a candidate this check would already
        // exclude.
        //
        // Fail-closed (ADR-0073, matching ADR-0070's precedent): a
        // candidate whose Player.BirthYear is null is EXCLUDED, not
        // included — xG Path cannot verify a null-BirthYear candidate
        // meets the new floor, and silently admitting it would be exactly
        // the "admit what can't be verified" failure mode ADR-0070/REQ-211's
        // fallback deliberately avoid elsewhere in this codebase. HasValue
        // check is required (not just `BirthYear >= MinBirthYear`, which
        // would evaluate false for null and coincidentally produce the
        // same excluded outcome) purely so this reads as an explicit
        // decision rather than an accident of nullable-int comparison
        // semantics.
        var playersById = await playerRepository.GetPlayersByIdsAsync(structurallyEligibleIds, cancellationToken);
        var birthYearEligibleIds = structurallyEligibleIds
            .Where(id => playersById.TryGetValue(id, out var player) &&
                         player.BirthYear.HasValue &&
                         player.BirthYear.Value >= MinBirthYear)
            .ToList();

        // REQ-1201/ADR-0079/S-161: Position != null/empty, applied here in
        // the SAME pass as BirthYear above (reusing the playersById fetch —
        // no second GetPlayersByIdsAsync call needed) rather than inside
        // IsEligible/PathCareerStintFilter, for the identical reason
        // BirthYear lives here rather than there: it's a fact about the
        // PLAYER (Player.Position), not about any individual
        // PlayerCareerStint row — stints have no Position of their own.
        // Fixes a real 2026-08-18 QA report: Player.Position staying null
        // forever for a subset of rows is deliberate, documented REQ-1207
        // behavior (a Wikidata data gap, not a bug), but nothing previously
        // stopped a null-Position candidate from being SELECTED as a
        // target in the first place, which let a preventable
        // "Position: not available" surface on a real puzzle screen.
        //
        // Fail-closed (ADR-0079, matching ADR-0073/ADR-0070's precedent): a
        // candidate whose Player.Position is null, empty, or whitespace-only
        // is EXCLUDED, not included — xG Path cannot verify such a
        // candidate has real Position data to show, and silently admitting
        // it would be exactly the "admit what can't be verified" failure
        // mode ADR-0070/REQ-211's fallback deliberately avoid elsewhere in
        // this codebase. IsNullOrWhiteSpace is used, not a bare null check,
        // as the null-tolerant-string equivalent of BirthYear's HasValue
        // guard immediately above — so this too reads as an explicit
        // decision, not an accident of string-comparison semantics.
        var birthYearAndPositionEligibleIds = birthYearEligibleIds
            .Where(id => playersById.TryGetValue(id, out var player) &&
                         !string.IsNullOrWhiteSpace(player.Position))
            .ToList();

        // ADR-0056: a real player-facing complaint ("I got this Austrian guy
        // I had no idea who he is") — the three structural checks above say
        // nothing about whether a candidate is actually recognizable, so a
        // long-but-obscure career passed them just as easily as a star's.
        // FilterFamiliarAsync never shrinks the pool below what's safe to
        // shrink to on its own (it fails open on a Wikidata failure or a
        // total data gap — see its own doc comment) — GenerateInstanceAsync's
        // existing "not enough eligible players" abort still covers the case
        // where familiarity filtering leaves too few candidates.
        var familiarIds = await playerFamiliarityService.FilterFamiliarAsync(birthYearAndPositionEligibleIds, cancellationToken);
        return birthYearAndPositionEligibleIds.Where(familiarIds.Contains).ToList();
    }

    // REQ-1201/ADR-0074/S-138's three independent structural checks (down
    // from the pre-S-138 shape, but NOT down to two — see
    // MinDocumentedStintCount's own comment: dropping the total-row floor
    // entirely, as the original backlog story assumed was safe, was found
    // during architecture/quality review to break REQ-1203's clue-turn
    // split for a 2-stint candidate, so it is RETAINED here, just
    // re-justified):
    //   - at least MinDocumentedStintCount (3) total documented stint rows,
    //     any clubs — required for REQ-1203's PathClueSequenceBuilder,
    //     which divides a target's full stint count across exactly 3 fixed
    //     club-reveal turns and assumes at least 3. NOT a re-statement of
    //     ADR-0045's original "3 distinct documented career club stints"
    //     reasoning (that textual question is now moot — REQ-1201's own
    //     rule no longer hinges on a literal "3" from REQ-1201's text) —
    //     this floor exists purely so every eligible target has enough
    //     documented career data for REQ-1203 to build a real 3-turn club
    //     reveal, independent of REQ-1201's own club-quality signal below.
    //   - "chronological order determinable from start/end dates": rejects
    //     a candidate if any two of their stints share an identical
    //     (StartYear, EndYear) pair (including two simultaneously "ongoing"
    //     stints, EndYear both null) — at that point
    //     IPlayerStoreRepository.AddCareerStintsAsync's persisted
    //     SequenceOrder between those two rows is an artifact of write
    //     order, not something actually derivable from the dates
    //     themselves, so "order determinable from start/end dates" fails
    //     for this candidate. Unchanged by S-138.
    //   - at least MinQualifyingSeededClubs (2) DISTINCT clubs present in
    //     the seeded ClubDefinition reference table (REQ-109), each
    //     individually meeting the appearance-count bar: at least
    //     MinAppearancesAtSeededClub games played there when that count is
    //     known (ADR-0047), or AppearanceCount unknown (a stint with no
    //     recorded AppearanceCount still counts, since "unknown" is not
    //     evidence of a fringe appearance; only a known, sub-threshold
    //     count disqualifies that stint). The count is over distinct
    //     qualifying club NAMES, not stint rows — a player with many stints
    //     at one seeded club (e.g. a loan, then a later permanent return)
    //     still only contributes ONE qualifying club, not two. Extra stints
    //     at non-seeded clubs, or at seeded clubs that individually fail
    //     the appearance bar, don't block eligibility as long as
    //     MinQualifyingSeededClubs distinct seeded clubs DO qualify.
    //   - ADR-0056: and, on top of the three structural checks above, the
    //     candidate is judged "familiar enough" via
    //     IPlayerFamiliarityService.FilterFamiliarAsync (see
    //     GetEligiblePlayerIdsAsync below) — none of the three checks here
    //     says anything about whether a player is one a casual player would
    //     recognize.
    private static bool IsEligible(IReadOnlyList<PlayerCareerStint> stints, IReadOnlySet<string> seededClubNames)
    {
        if (stints.Count < MinDocumentedStintCount)
            return false;

        var datePairs = stints.Select(s => (s.StartYear, s.EndYear)).ToList();
        if (datePairs.Count != datePairs.Distinct().Count())
            return false;

        var qualifyingSeededClubCount = stints
            .Where(s =>
                seededClubNames.Contains(s.ClubName) &&
                (s.AppearanceCount is null || s.AppearanceCount >= MinAppearancesAtSeededClub))
            .Select(s => s.ClubName)
            .Distinct()
            .Count();

        return qualifyingSeededClubCount >= MinQualifyingSeededClubs;
    }
}
