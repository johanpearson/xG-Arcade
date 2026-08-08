using System.Text.RegularExpressions;
using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath;

// REQ-1203, bug fix (2026-08-08, reported via user testing): a read-time
// defensive filter for youth/age-grade national-team PlayerCareerStint rows
// that were persisted BEFORE the 2026-08-02 SPARQL-level fix
// (WikidataClient.BuildPlayerCareerStintsByQidsQuery's
// `MINUS { ?club wdt:P31/wdt:P279* wd:{NationalTeamClassWikidataQid} }`
// clause) started excluding any national team — senior or youth — from
// being fetched at all. That fix only stops NEW rows from being written.
// PlayerCareerStintRefreshService.BuildNewStintsByPlayerId is documented
// "additive only, never a wipe-and-replace" (see its own doc comment), so
// any national-team row fetched before 2026-08-02 is still sitting in the
// ~608K-row PlayerCareerStint table and nothing removes it — screenshots
// from real user testing showed rows like "Spain national under-16
// association football team" and "Italy national under-20/under-21
// football team" leaking into xG Path's club-reveal clues, directly
// violating REQ-1203's "national team caps/appearances are never revealed
// as a clue for this game" acceptance criterion.
//
// Deliberately a READ-time filter, not a DELETE/cleanup script — unlike
// ADR-0059's DuplicateCareerStintCleaner (docs/decisions/0059-career-stint-
// club-name-canonicalization.md), there is no QID stored on already-
// persisted rows to prove a match against (WikidataCareerStintEntry.ClubQid
// only started being threaded through at write time from 2026-08-04
// onward, and even that identifies the underlying ?club, not whether a
// given already-written row is/isn't a national team), so a name-based
// DELETE against 608K rows would not be "provable" the way ADR-0059's
// canonical-name-exists cleanup was. A name-based filter is fine for
// read-time exclusion — a false positive there just means one clue is
// skipped — but not fine for an irreversible row deletion.
//
// Scope: youth/age-grade national teams ONLY, matching the actual reported
// symptom — not every national team, and not non-FIFA regional sides. Real
// screenshots reviewed alongside this fix showed "Italy men's national
// association football team" (the senior team) rendering correctly right
// where it belongs in the same puzzle timeline, and a "Basque Country
// regional football team" stint appearing without being flagged as a
// problem. Matching only "national" + an age-grade "under-N" marker keeps
// this filter provably safe against the case that matters most (never
// hiding a real club or the valid senior-team clue) — a broader "any
// national team" filter risks silently swallowing a leftover senior-team
// row nobody has actually reported as wrong, which would be a new,
// unreported correctness regression, not a fix for the one that was
// reported.
public static class PathCareerStintFilter
{
    // Wikidata's English label convention for age-grade national sides is
    // "<Country> national under-<N> [association] football team" (e.g.
    // "Spain national under-16 association football team", "Italy
    // national under-20 football team", "Italy national under-21 football
    // team" — all three straight from the reported screenshots). Matching
    // "national" followed by an "under-<digits>" marker, rather than
    // "national ... team" alone, is what keeps this from also matching the
    // valid senior-team case ("Italy men's national association football
    // team" has no "under-N" marker at all).
    //
    // The leading \b before "national" is required: without it, the pattern
    // matches "national" as a bare substring anywhere it occurs, including
    // at the tail of a longer word — e.g. "International Under-20 Select
    // XI", "FC International Milan Under-20", and "Multinational
    // Development Squad Under-19" all contain "...national" via
    // "Inter"+"national"/"Multi"+"national" followed later by an
    // "under-N" marker, and would be wrongly excluded despite not being
    // national teams at all. The leading \b anchors the match to a real
    // word boundary so "national" must start its own word.
    //
    // NOT verified against a live Wikidata query from this sandbox (no
    // wikidata.org access here) — this pattern is inferred from the
    // reported label text only. Flagged for manual confirmation against
    // real production PlayerCareerStint rows if this is found to under- or
    // over-match in practice.
    private static readonly Regex YouthNationalTeamPattern =
        new(@"\bnational\s.*\bunder-\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsYouthNationalTeam(string clubName) =>
        YouthNationalTeamPattern.IsMatch(clubName);

    // Excludes youth/age-grade national-team rows from an already-fetched
    // stint list — used at every read site that turns PlayerCareerStint
    // rows into either a puzzle's clue content (PathEndpoints.cs) or an
    // eligibility decision (XGPathGameModule.GetEligiblePlayerIdsAsync),
    // so the filter lives in exactly one place rather than being
    // copy-pasted at each call site.
    public static IReadOnlyList<PlayerCareerStint> ExcludeYouthNationalTeams(
        IReadOnlyList<PlayerCareerStint> stints) =>
        stints.Count == 0
            ? stints
            : stints.Where(s => !IsYouthNationalTeam(s.ClubName)).ToList();
}
