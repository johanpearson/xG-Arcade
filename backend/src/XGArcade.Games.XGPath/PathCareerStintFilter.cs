using System.Text.RegularExpressions;
using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath;

// REQ-1203, bug fix (2026-08-08, reported via user testing; broadened
// 2026-08-10, bug-bundle): a read-time defensive filter for national-team
// PlayerCareerStint rows that were persisted BEFORE the 2026-08-02
// SPARQL-level fix (WikidataClient.BuildPlayerCareerStintsByQidsQuery's
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
// Scope, CORRECTED 2026-08-10 (bug-bundle): the 2026-08-08 fix deliberately
// scoped this filter to youth/age-grade national teams ONLY, on the
// judgment (from screenshots reviewed at the time) that senior national
// teams were rendering correctly. A 2026-08-10 bug report — screenshot
// showing "Italy men's national association football team" with "30 apps"
// leaking into a club-reveal clue — directly contradicts that judgment and
// REQ-1203's own unqualified acceptance criterion ("national team
// caps/appearances are never revealed as a clue for this game — this clue
// type does not exist for xG Path" makes no senior/youth distinction). This
// filter now matches ANY national team, senior or youth — the same
// semantic WikidataClient's write-time MINUS clause already applies for
// new fetches (see that file's own comment near
// NationalTeamClassWikidataQid), just re-implemented as a label-based
// heuristic here because there is no QID on old persisted rows to check
// against directly (see the "why a read-time regex filter, not a QID-based
// DB query" reasoning two paragraphs up, unchanged).
//
// Still deliberately NOT "any label containing the word 'national'" — see
// NationalTeamPattern's own comment for the word-boundary care taken to
// avoid over-matching a real club whose name happens to contain "national"
// as a substring (e.g. "International", "Multinational"), or a genuine
// club literally named "National" with no accompanying "team" word. Still
// leaves non-FIFA regional representative sides alone — a "Basque Country
// regional football team" is not a national team and stays a valid clue
// (existing test case for this, preserved unchanged below).
public static class PathCareerStintFilter
{
    // Wikidata's English label convention for a national representative
    // side — senior OR youth/age-grade — always pairs the word "national"
    // with a trailing "team" (e.g. "Spain national under-16 association
    // football team", "Italy men's national association football team",
    // "Switzerland men's national football team", "Spain national football
    // team"). Matching "national" ... "team" as two independent word-
    // bounded tokens (not requiring a specific fixed phrase between them)
    // is what lets this pattern cover every observed shape — with or
    // without an age-grade "under-N" marker, with or without a "men's"/
    // "women's" marker, with or without "association" — without needing a
    // combinatorial list of exact phrasings.
    //
    // Both \b anchors matter, independently:
    //   - The leading \b before "national" stops the pattern from matching
    //     "national" as a bare substring inside a longer word — e.g.
    //     "International Under-20 Select XI", "FC International Milan
    //     Under-20", and "Multinational Development Squad Under-19" all
    //     contain "...national" via "Inter"+"national"/"Multi"+"national",
    //     and would be wrongly excluded despite not being national teams
    //     at all. The leading \b anchors the match to a real word boundary
    //     so "national" must start its own word.
    //   - The trailing \b before "team" (and requiring "team" as its own
    //     word, not just any occurrence of the four characters) is what
    //     keeps a genuine club literally named "National" (no accompanying
    //     "team" word in its label) from matching, and is also why a
    //     "Basque Country regional football team" — which never contains
    //     the word "national" at all — is correctly left alone regardless
    //     of this trailing check.
    //
    // NOT verified against a live Wikidata query from this sandbox (no
    // wikidata.org access here) — this pattern is inferred from the
    // reported label text only. Flagged for manual confirmation against
    // real production PlayerCareerStint rows if this is found to under- or
    // over-match in practice.
    private static readonly Regex NationalTeamPattern =
        new(@"\bnational\b.*\bteam\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsNationalTeam(string clubName) =>
        NationalTeamPattern.IsMatch(clubName);

    // Excludes national-team rows (any, not just youth/age-grade — see this
    // class's own 2026-08-10 scope-correction comment above) from an
    // already-fetched stint list — used at every read site that turns
    // PlayerCareerStint rows into either a puzzle's clue content
    // (PathEndpoints.cs) or an eligibility decision
    // (XGPathGameModule.GetEligiblePlayerIdsAsync), so the filter lives in
    // exactly one place rather than being copy-pasted at each call site.
    public static IReadOnlyList<PlayerCareerStint> ExcludeNationalTeams(
        IReadOnlyList<PlayerCareerStint> stints) =>
        stints.Count == 0
            ? stints
            : stints.Where(s => !IsNationalTeam(s.ClubName)).ToList();
}
