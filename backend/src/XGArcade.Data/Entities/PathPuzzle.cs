namespace XGArcade.Data.Entities;

// Games.XGPath (COMP-11) entity — one puzzle within a PathInstance, and the
// "cell" IGameModule.GetCellIdsAsync returns for this game (PathPuzzle.Id =
// cell id, matching that interface's existing contract — same precedent as
// GridCell.Id).
//
// TargetPlayerId is the specific eligible player (REQ-1201) this puzzle
// targets, fixed once at generation time — unlike GridCell (which has no
// single fixed answer, only two category constraints checked at guess
// time), an xG Path puzzle always has exactly one correct target, so an FK
// to Player is meaningful here. This crosses from Games.XGPath (COMP-11)
// into Player's table (COMP-06/Data.PlayerStore), which is a different
// boundary than ADR-0003's Core/game FK omission — that ADR is specifically
// about `Round` (XGArcade.Core) never holding a game-specific FK; a game
// module referencing shared player data is the same kind of cross-reference
// PlayerCareerStint/PlayerAttribute/etc. already have to Player, just from
// the other side. Cascade delete mirrors those existing Player-referencing
// FKs (XGArcadeDbContext.OnModelCreating) — there is no player-row-deletion
// pathway in the codebase today, so this is a defensive/theoretical case,
// not one currently exercised.
//
// No clue/reveal fields yet — that's REQ-1203/S-082, not this story.
public class PathPuzzle
{
    public Guid Id { get; set; }
    public required Guid PathInstanceId { get; set; }
    public required Guid TargetPlayerId { get; set; }
}
