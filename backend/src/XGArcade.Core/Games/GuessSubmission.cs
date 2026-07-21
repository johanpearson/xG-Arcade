namespace XGArcade.Core.Games;

// The submission shape Core.Scoring hands to IGameModule.ScoreSubmissionAsync
// via its object-typed `submission` parameter — concrete like RoundConfig/
// GameInstance/ScoreResult, not because IGameModule's signature is
// GuessSubmission-specific (it deliberately stays `object`, since only one
// game exists today — ADR-0003's "generalize when a second game arrives"
// principle applies here too, same as Guess.CellId's own accepted
// simplification).
//
// REQ-209/REQ-210: ChosenPlayerId is set only on a resubmission that answers
// a disambiguation prompt (ScoreResult.DisambiguationCandidates from the
// prior call on the same attempt) — null on every ordinary submission. Never
// trusted blindly: the owning game module re-verifies it server-side against
// the cell's categories on every call, since data can change between the
// prompt and the resubmission.
public sealed record GuessSubmission(Guid CellId, string SubmittedName, Guid? ChosenPlayerId = null);
