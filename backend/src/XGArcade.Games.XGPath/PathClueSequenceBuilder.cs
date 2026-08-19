using System.Globalization;
using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath;

// REQ-1203/S-082: builds the fixed, ordered 7-turn clue sequence for one xG
// Path puzzle's target player. Deliberately pure — no repository/DbContext
// access, no IGameModule dependency — so test-writer can unit test the
// split/format/reveal-count logic directly (REQ1203-named tests) without
// spinning up the whole module or a database. The caller (XGPathGameModule
// or, today, PathEndpoints per ADR-0016) is responsible for fetching the
// PlayerCareerStint list, Position, nationality, and BirthYear this needs.
public static class PathClueSequenceBuilder
{
    // REQ-1203/REQ-1205/REQ-1206: the fixed total turn count every xG Path
    // puzzle has, regardless of its target's own stint count N — 3
    // club-reveal turns + 1 bundled year-range turn + 3 fixed
    // (position/nationality/age) turns.
    public const int TotalTurns = 7;

    private const int ClubRevealTurnCount = 3;

    // REQ-1207: a null Position/nationality/BirthYear renders as this
    // literal string — the clue is still revealed, never skipped or
    // omitted, so a data gap never shrinks the fixed 7-turn sequence.
    private const string NotAvailable = "not available";

    // REQ-1203: every one of the target's N documented stints, spread
    // across exactly 3 club-reveal turns (turns 1-3), then one bundled
    // year-range turn covering all N clubs (turn 4), then Position,
    // Nationality, Age(/BirthYear) in that fixed order (turns 5-7).
    // stintsChronological must already be sorted ascending by
    // SequenceOrder (PlayerCareerStint's own doc comment) — this method
    // does not re-sort; XGPathGameModule/PathEndpoints are expected to have
    // fetched them via the repository shape that already guarantees this
    // (GetCareerStintsByPlayerIdsAsync's underlying rows are written with a
    // resolved SequenceOrder by IPlayerStoreRepository.AddCareerStintsAsync).
    public static IReadOnlyList<PathClueTurn> BuildSequence(
        IReadOnlyList<PlayerCareerStint> stintsChronological,
        string? position,
        string? nationality,
        int? birthYear)
    {
        var turns = new List<PathClueTurn>(TotalTurns);

        var clubTurnSizes = SplitIntoTurns(stintsChronological.Count);
        var cursor = 0;
        foreach (var size in clubTurnSizes)
        {
            var clubsInTurn = stintsChronological
                .Skip(cursor)
                .Take(size)
                .Select(s => new PathClubClue(s.ClubName, s.AppearanceCount,
                    IsLoan: PathCareerStintFilter.IsInferredLoan(s, stintsChronological)))
                .ToList();
            cursor += size;

            turns.Add(new PathClueTurn(turns.Count + 1, PathClueKind.ClubReveal, Clubs: clubsInTurn));
        }

        // REQ-1203: turn 4, once all 3 club-reveal turns have happened —
        // every club revealed so far (which, by construction, is every one
        // of the N stints, since the 3 club-reveal turns above always
        // exhaust the full list) gets its own year-range entry, same
        // chronological order, bundled into one turn rather than one clue
        // per club.
        var yearRanges = stintsChronological.Select(FormatYearRange).ToList();
        turns.Add(new PathClueTurn(turns.Count + 1, PathClueKind.YearRange, YearRanges: yearRanges));

        // REQ-1203: fixed order, one at a time — position, then
        // nationality, then age. REQ-1207's null contract: rendered as
        // "not available," never a skipped turn.
        turns.Add(new PathClueTurn(turns.Count + 1, PathClueKind.Position, TextValue: position ?? NotAvailable));
        turns.Add(new PathClueTurn(turns.Count + 1, PathClueKind.Nationality, TextValue: nationality ?? NotAvailable));
        // "Age (or birth year)" (REQ-1203's own wording leaves the choice
        // open): this renders the raw BirthYear rather than a computed age,
        // deliberately — computing "age" would need a notion of "now" (a
        // TimeProvider dependency) threaded into what is otherwise a pure,
        // clock-free builder, for a value REQ-1207 already stores directly.
        // Flagged as a judgment call, not literal REQ text.
        turns.Add(new PathClueTurn(turns.Count + 1, PathClueKind.Age,
            TextValue: birthYear?.ToString(CultureInfo.InvariantCulture) ?? NotAvailable));

        return turns;
    }

    // Inference, not literal REQ text (flagged for architecture-reviewer,
    // see docs/backlog.md S-082/REQ-1203's own acceptance criteria on "the
    // sequence halts immediately on a correct guess"): the literal
    // suggestion "revealed-turn-count = min(attemptsMade + 1, 7)" was
    // checked against that halt-immediately rule and found to over-reveal
    // by one turn for a SOLVED puzzle — e.g. a puzzle solved on the very
    // first attempt (attemptsMade=1) would compute min(2,7)=2 revealed
    // turns under that formula, exposing turn 2 even though the player
    // never needed it and the puzzle ended after only turn 1 was ever
    // shown. This method instead branches on isCorrect:
    //   - Not yet correct: turn 1 is visible before any guess (attemptsMade
    //     0 -> 1 revealed); each wrong guess reveals the next turn
    //     (attemptsMade N -> N+1 revealed), capped at 7 once every attempt
    //     is exhausted (REQ-1205).
    //   - Correct: the winning guess was submitted while exactly
    //     attemptsMade turns were visible (turn k is revealed in
    //     preparation for attempt k, so attempt k is made when k turns are
    //     showing) — no further turn is ever revealed once solved
    //     (REQ-1203's "no further clue is ever revealed once the puzzle is
    //     solved"), so the revealed count freezes at attemptsMade rather
    //     than attemptsMade + 1.
    // Both branches cap at TotalTurns (7) as a defensive bound; attemptsMade
    // should never itself exceed 7 (REQ-1205's cap), but the cap here
    // guards against a malformed/legacy Guess row rather than trusting that
    // invariant blindly.
    public static int GetRevealedTurnCount(int attemptsMade, bool isCorrect) =>
        isCorrect
            ? Math.Min(attemptsMade, TotalTurns)
            : Math.Min(attemptsMade + 1, TotalTurns);

    // REQ-1203's N-way split: base = N div 3, remainder = N mod 3; the
    // first (3 - remainder) turns each get `base` clubs, the last
    // `remainder` turns each get `base + 1` — turn sizes non-decreasing, so
    // the first turn is never larger than the last. Matches every worked
    // example in REQ-1203's own text (N=3 -> 1-1-1; N=4 -> 1-1-2; N=5 ->
    // 1-2-2; N=10 -> 3-3-4; N=11 -> 3-4-4).
    private static IReadOnlyList<int> SplitIntoTurns(int stintCount)
    {
        var baseSize = stintCount / ClubRevealTurnCount;
        var remainder = stintCount % ClubRevealTurnCount;

        var sizes = new int[ClubRevealTurnCount];
        for (var i = 0; i < ClubRevealTurnCount; i++)
            sizes[i] = baseSize;

        for (var i = ClubRevealTurnCount - remainder; i < ClubRevealTurnCount; i++)
            sizes[i]++;

        return sizes;
    }

    // REQ-1203's own worked example: "2012-15, 2015-19, 2019-present" — full
    // 4-digit start year, 2-digit (zero-padded) end year, or the literal
    // "present" for a null (ongoing) EndYear. The exact display convention
    // beyond that one worked example is an inference, not literal REQ text
    // — flagged for manual/design review, same as every other display-
    // format judgment call in this method.
    private static string FormatYearRange(PlayerCareerStint stint)
    {
        var end = stint.EndYear is null
            ? "present"
            : (stint.EndYear.Value % 100).ToString("D2", CultureInfo.InvariantCulture);

        return $"{stint.StartYear}-{end}";
    }
}
