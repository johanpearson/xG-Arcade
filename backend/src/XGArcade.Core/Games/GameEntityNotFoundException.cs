namespace XGArcade.Core.Games;

// Base type for "this id doesn't resolve to a real game entity" signals — a
// submitted cellId/instanceId/puzzleId that is malformed or stale, not an
// ordinary gameplay outcome. Defined here, in Core.Games, rather than in
// each throwing game module's own assembly (e.g. XGArcade.Games.XGGrid,
// XGArcade.Games.XGPath) so the shared caller — XGArcade.Api.Guesses.
// GuessEndpoints, which is game-agnostic by design and routes through
// IGuessSubmissionService/IGameModuleResolver by round.GameKey — never needs
// compile-time knowledge of each game's own concrete exception type to catch
// this failure mode. Mirrors LiveLookupUnavailableException's reasoning for
// living in Core rather than a game module's assembly, though the caller
// here is the API layer rather than Core itself.
//
// Thrown by concrete, game-module-owned subtypes — GuessScoringException
// (XGArcade.Games.XGGrid) and PathScoringException (XGArcade.Games.XGPath)
// — from IGameModule.ScoreSubmissionAsync/GetCellIdsAsync implementations
// when a submitted id doesn't resolve to a real cell/puzzle within the
// given game instance. Game modules keep throwing their own concrete type
// (useful for game-specific log messages/tests); only the shared catch site
// needs to reason about this common base.
//
// Caught by GuessEndpoints, which logs the concrete exception server-side
// and returns a bare 404 — the same response shape as an unresolved
// roundId, since both are plain "this id doesn't resolve to anything"
// outcomes.
public class GameEntityNotFoundException(string message) : Exception(message);
