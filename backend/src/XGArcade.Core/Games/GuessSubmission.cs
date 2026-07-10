namespace XGArcade.Core.Games;

// The submission shape Core.Scoring hands to IGameModule.ScoreSubmissionAsync
// via its object-typed `submission` parameter — concrete like RoundConfig/
// GameInstance/ScoreResult, not because IGameModule's signature is
// GuessSubmission-specific (it deliberately stays `object`, since only one
// game exists today — ADR-0003's "generalize when a second game arrives"
// principle applies here too, same as Guess.CellId's own accepted
// simplification).
public sealed record GuessSubmission(Guid CellId, string SubmittedName);
