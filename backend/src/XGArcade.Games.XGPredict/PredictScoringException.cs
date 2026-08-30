using XGArcade.Core.Games;

namespace XGArcade.Games.XGPredict;

// Thrown when an instanceId or cellId (match id) in
// ScoreSubmissionAsync/GetCellIdsAsync doesn't resolve to a real
// PredictInstance/PredictMatch — a malformed/stale request, not an ordinary
// gameplay outcome. Mirrors PathScoringException's/GuessScoringException's
// naming/role for the equivalent "not found" failure mode in this game
// module.
//
// Derives from Core.Games.GameEntityNotFoundException (not System.Exception
// directly), matching PathScoringException's/GuessScoringException's actual
// precedent — quality-gate fix, 2026-08-30: an earlier version of this
// comment claimed ADR-0096 §4 decided to derive directly from Exception,
// which was a misattribution (ADR-0096 never made that decision; it only
// says this exception mirrors the Grid/Path "not found" convention, and
// that convention's actual base type is GameEntityNotFoundException).
// GameEntityNotFoundException exists precisely so a future game-agnostic
// catch site (e.g. XGArcade.Api.Guesses.GuessEndpoints) doesn't need
// per-game knowledge to catch this failure mode — "no caller exists yet"
// is not a reason to skip that base type, the same as it wasn't when
// PathScoringException first derived from it.
//
// Deliberately does NOT cover invalid-submission validation (negative goal
// counts, REQ-1302) — that is a different failure mode (not an id-resolution
// problem at all) and has its own type, PredictInvalidSubmissionException.
public class PredictScoringException(string message) : GameEntityNotFoundException(message);
