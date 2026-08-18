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
// club literally named "National" with no accompanying "team" word.
//
// CORRECTED (2026-08-10 follow-up, quality-gate finding): the previous
// version of this comment claimed this filter "leaves non-FIFA regional
// representative sides alone," which overstated what the regex actually
// does. This filter has no data source for FIFA/federation affiliation at
// all — it only ever sees a ClubName string — and does NOT special-case
// it. It matches purely on Wikidata label WORDING: any label containing
// both "national" and "team" as word-bounded tokens (see NationalTeamPattern
// above) is excluded, regardless of whether the side is actually
// FIFA-affiliated. A non-FIFA side whose Wikidata label nonetheless uses
// "national team" phrasing — e.g. "Catalonia national football team" (NOT
// verified against a live Wikidata query from this sandbox; flagged for
// manual confirmation) — is excluded the same as any FIFA member national
// team. REQ-1203's own acceptance criterion ("national team caps/
// appearances are never revealed as a clue" — no FIFA-affiliation
// qualifier anywhere in the requirement text) supports reading this as the
// correct, not over-broad, behavior: excluding anything self-described in
// its own label as a "national team" is safer and more REQ-consistent than
// trying to encode FIFA membership this filter has no way to check anyway.
// "Basque Country regional football team" stays a valid clue ONLY because
// its label uses "regional" and never triggers the "national" + "team"
// match in the first place — that is not a general carve-out for non-FIFA
// sides, just the one existing test case's specific wording (preserved
// unchanged below). See
// REQ1203_IsNationalTeam_NonFifaButLabeledAsNationalTeam_ReturnsTrue for
// the test that pins down this exact boundary.
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

    // S-139 (Epic 12, 2026-08-18), REQ-1203/ADR-0075: a B-team/reserve-team
    // exclusion, same read-time-filter shape and same reasoning as
    // NationalTeamPattern/IsNationalTeam/ExcludeNationalTeams above (no
    // change to that reasoning — see this class's own doc comment for why
    // a read-time regex filter, not a QID-based DB query or a DELETE, is
    // the right tool here). No B-team concept exists anywhere in this
    // schema — ClubDefinition has no type/tier field, and no B-team club is
    // seeded — so a stint at a reserve/B side (e.g. "Real Madrid Castilla,"
    // "Barcelona Atlètic," "Manchester United U21") currently passes every
    // eligibility check unfiltered and can surface as a raw clue-reveal
    // club name, the same REQ-1203 "don't leak a non-answer-worthy team
    // name as a clue" violation the national-team filter above closes for
    // representative sides.
    //
    // Wikidata's English label convention for a reserve/development side
    // takes several different shapes, none of which share a single common
    // word the way national teams reliably pair "national" with "team":
    //   - An explicit tier/reserve word: "reserve"/"reserves" (e.g.
    //     "Everton Reserves"), or a bare "B" (e.g. "Barcelona B") or "II"
    //     (e.g. "Bayern Munich II") used as the club's own tier suffix.
    //   - An age-grade youth-squad marker that is also, in practice, used
    //     as a club's senior-adjacent development side in some labels:
    //     "U17"–"U19" and "U20"–"U23" (covering the age range actually seen
    //     in reserve/development squad labels; deliberately narrower than
    //     NationalTeamPattern's own "any under-N" youth-team matching,
    //     since that pattern's job is catching youth NATIONAL teams, not
    //     bounding what counts as a development-squad age).
    //   - A language-specific reserve-side name with no shared word at all:
    //     Spanish "Castilla" (Real Madrid's reserve side) and
    //     Catalan/Spanish "Atlètic"/"Atlético" as used specifically as a
    //     reserve-team qualifier (e.g. "Barcelona Atlètic") — note this is
    //     a DIFFERENT use of "Atlético" than a senior club's own proper
    //     name (e.g. "Atlético Madrid," a full first-team club, not a
    //     reserve side of anything), which is exactly why this pattern
    //     requires "atlètic"/"atletic" as its own trailing word (see the
    //     \b discussion below) rather than matching it as a bare substring.
    //
    // Because there is no single shared word across all these shapes (unlike
    // NationalTeamPattern's "national"..."team" pair), this pattern is an
    // alternation of independent tokens, each still individually
    // word-bounded on both sides:
    //   - Leading \b matters for every alternative the same way it matters
    //     for NationalTeamPattern's leading \b before "national": it stops
    //     the pattern matching a short token as a bare substring inside a
    //     longer word. Without it, "B" would match inside "Bayer,"
    //     "Bayern," "Borussia," etc.; "II" would match inside any club name
    //     that happens to contain the two letters "ii" consecutively; and
    //     "atl[eè]tic" would match the first 7 letters of "Atletico" even
    //     though "Atletico" (no trailing "team"/space break after
    //     "-tic") is one contiguous word.
    //   - Trailing \b matters the same way: without it, "B" would match the
    //     leading "B" inside "Barcelona" itself, and "atl[eè]tic" would
    //     match as a substring prefix of "Atletico" (the characters
    //     "Atletic" followed immediately by "o," with no word-boundary
    //     between "c" and "o") — this is exactly why "Atletico Madrid" (a
    //     genuine, first-team, seeded club — see ReferenceDataSeeder.cs)
    //     does NOT match this pattern despite sharing a 7-letter prefix
    //     with "atlètic": the trailing \b fails inside "Atletico" because
    //     "c" and "o" are both word characters with no boundary between
    //     them, so only a label that has "atlètic"/"atletic" as its own
    //     standalone final word (e.g. "Barcelona Atlètic") matches.
    //   - "RB Leipzig" (a genuine, first-team, seeded club) does not match
    //     the bare "B" alternative for the same reason: "R" and "B" are
    //     adjacent word characters in "RB" with no boundary between them,
    //     so \bB\b never matches the "B" inside "RB." Only a label where
    //     "B" appears as its own space-separated word (e.g. "Barcelona B")
    //     matches.
    //
    // Verified by hand against the current 33-club seeded list
    // (ReferenceDataSeeder.cs's Clubs array, as of S-139/2026-08-18: Real
    // Madrid, Barcelona, Manchester United, Manchester City, Liverpool,
    // Arsenal, Chelsea, Bayern Munich, Borussia Dortmund, Juventus, AC
    // Milan, Inter Milan, Paris Saint-Germain, Ajax, Benfica, Tottenham
    // Hotspur, Atletico Madrid, Napoli, AS Roma, Sevilla, Porto, RB
    // Leipzig, Bayer Leverkusen, Marseille, Lyon, Monaco, Lille, Lazio,
    // Valencia, Real Sociedad, Newcastle United, West Ham United, Celtic)
    // — none contain "reserve(s)," a standalone "B" or "II" token, "U17"–
    // "U23," "castilla," or "atl[eè]tic" as their own word. See ADR-0075
    // for this check recorded as a decision artifact, not just this code
    // comment.
    //
    // Deliberately a CONSERVATIVE, imperfect heuristic, not a complete
    // B-team/reserve-team taxonomy — it is inferred from a handful of known
    // label shapes, not an exhaustive survey of how every football
    // federation's reserve sides are labeled on Wikidata. A bare "B" or
    // "II" token in particular is a real false-positive risk against a
    // genuinely-named (non-reserve) club that is not in today's 33-club
    // seeded list but could be added later (e.g. Faroese "B36 Tórshavn"-
    // style names use "B" as part of a proper name, not a reserve-tier
    // marker) — not a problem today, since no such club is seeded, but
    // worth flagging for whoever next expands the seeded list. Like
    // NationalTeamPattern above, this is expected to be refined
    // iteratively as real false positives/negatives surface against
    // production data, the same way the national-team filter itself needed
    // two follow-up corrections (2026-08-10 broadening the age-grade scope
    // to senior teams too, after a real bug report; and a Catalonia/Basque
    // wording inconsistency found later and tracked as S-140, not yet
    // fixed) rather than being solved correctly in one pass — see this
    // class's own top-of-file doc comment for that history.
    //
    // NOT verified against a live Wikidata query from this sandbox (no
    // wikidata.org access here) — this pattern is inferred from the known
    // reserve/B-team label shapes described above only, not from a survey
    // of real PlayerCareerStint rows. Flagged for manual confirmation
    // against real production data if this is found to under- or
    // over-match in practice.
    private static readonly Regex BTeamPattern =
        new(@"\b(reserves?|B|II|U1[7-9]|U2[0-3]|castilla|atl[eè]tic)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBTeam(string clubName) => BTeamPattern.IsMatch(clubName);

    // Excludes B-team/reserve-team rows from an already-fetched stint list —
    // same two read sites as ExcludeNationalTeams above
    // (PathEndpoints.cs's clue-reveal path and
    // XGPathGameModule.GetEligiblePlayerIdsAsync's eligibility check), and
    // deliberately called ALONGSIDE ExcludeNationalTeams at both sites, not
    // as a replacement for it — the two filters exclude different, disjoint
    // categories of non-answer-worthy "club" rows (national/representative
    // sides vs. reserve/development sides) and both need to run.
    public static IReadOnlyList<PlayerCareerStint> ExcludeBTeams(
        IReadOnlyList<PlayerCareerStint> stints) =>
        stints.Count == 0
            ? stints
            : stints.Where(s => !IsBTeam(s.ClubName)).ToList();
}
