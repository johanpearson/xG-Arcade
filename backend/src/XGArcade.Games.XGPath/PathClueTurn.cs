namespace XGArcade.Games.XGPath;

// REQ-1203/S-082: the discriminator for PathClueTurn.Kind — one value per
// distinct clue shape in the fixed 7-turn sequence. Order here is
// documentation only; the actual turn order is whatever
// PathClueSequenceBuilder.BuildSequence emits (TurnNumber), never inferred
// from this enum's declaration order.
public enum PathClueKind
{
    ClubReveal,
    YearRange,
    Position,
    Nationality,
    Age,
}

// REQ-1203: one club revealed within a ClubReveal turn — AppearanceCount is
// null exactly when Wikidata's P1350 qualifier wasn't recorded for this
// stint (ADR-0042/PlayerCareerStint's own "count unknown, never a
// misleading 0" rule); the club is still revealed either way, never
// delayed or omitted for a missing count.
public record PathClubClue(string ClubName, int? AppearanceCount);

// REQ-1203/S-082: one turn of the fixed 7-turn clue-reveal sequence.
// TurnNumber is 1-based and matches this turn's position in
// PathClueSequenceBuilder.BuildSequence's output (also its 1-based ordinal
// for GetRevealedTurnCount's gating). Exactly one of the payload fields
// below is populated, selected by Kind:
//   - ClubReveal: Clubs (one turn's worth of PathClubClue, REQ-1203's
//     base/remainder split, chronological within the turn)
//   - YearRange: YearRanges (one entry per club, in the SAME chronological
//     order as every club revealed across all 3 ClubReveal turns combined
//     — REQ-1203's single bundled clue, never one clue per club)
//   - Position/Nationality/Age: TextValue (REQ-1207's "null renders as
//     'not available,' never a skipped turn" contract — TextValue is never
//     itself null; a missing source value is rendered as the literal
//     string "not available" before this record is even constructed)
public record PathClueTurn(
    int TurnNumber,
    PathClueKind Kind,
    IReadOnlyList<PathClubClue>? Clubs = null,
    IReadOnlyList<string>? YearRanges = null,
    string? TextValue = null);
