namespace XGArcade.Games.XGPredict;

// ADR-0096 §4: thrown when an instanceId or cellId (match id) in
// ScoreSubmissionAsync/GetCellIdsAsync doesn't resolve to a real
// PredictInstance/PredictMatch, or when a submitted prediction's goal
// counts are invalid (negative — REQ-1302). Mirrors PathScoringException's/
// GuessScoringException's naming/role for the equivalent "not found" or
// "invalid submission" failure mode in this game module. Deliberately
// derives directly from Exception, not Core.Games.GameEntityNotFoundException
// — no Core-side caller exists yet to catch a shared base type (ADR-0096
// §4/Context item 3), unlike PathScoringException's own shared-catch-clause
// reasoning.
public class PredictScoringException(string message) : Exception(message);
