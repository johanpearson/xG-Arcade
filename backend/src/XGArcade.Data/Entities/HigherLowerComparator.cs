namespace XGArcade.Data.Entities;

// Games.XGHigherLower (COMP-18) entity — one position in a
// HigherLowerInstance's fixed, fully-ordered comparator sequence (REQ-1502/
// 1503), and the "cell" IGameModule.GetCellIdsAsync returns for this game
// (HigherLowerComparator.Id = cell id, same precedent as PredictMatch.Id/
// PathPuzzle.Id/GridCell.Id).
//
// SequencePosition is 0-based and unique within a given
// HigherLowerInstanceId — the order in which a participant plays through
// this Round's comparators, immediately following the instance's own fixed
// baseline (position -1, conceptually; the baseline lives directly on
// HigherLowerInstance, not as position 0 here). PlayerId/Value are this
// position's fixed player and their real recorded effective count for the
// instance's StatCategory (REQ-1501), guaranteed (by REQ-1502, checked once
// at generation time, never re-evaluated per participant) to differ from
// the immediately preceding position's Value (no exact tie — no valid
// Higher/Lower answer) and to never repeat a player already used earlier in
// the same sequence, including the baseline.
public class HigherLowerComparator
{
    public Guid Id { get; set; }
    public required Guid HigherLowerInstanceId { get; set; }
    public required int SequencePosition { get; set; }
    public required Guid PlayerId { get; set; }
    public required int Value { get; set; }
}
