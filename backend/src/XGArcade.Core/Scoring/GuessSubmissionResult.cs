using XGArcade.Core.Games;

namespace XGArcade.Core.Scoring;

// Every distinct reason a submission can be rejected — REQ-202's "distinct,
// specific reason — never a generic message" applies to how the API layer
// maps each of these, not just to the guess-change-policy case it was
// written about.
//
// NeedsDisambiguation (REQ-209) is deliberately not a rejection in the same
// sense as the others below — nothing about the submission was wrong, the
// player's guess just resolved to more than one fitting candidate and needs
// a follow-up choice. It's listed here rather than folded into Accepted
// because IsCorrect/AttemptCount/Locked are all meaningless for it (REQ-210:
// showing the prompt is not itself a scored attempt).
public enum GuessSubmissionOutcome
{
    Accepted,
    NeedsDisambiguation,
    RoundNotFound,
    RoundNotActive,
    CellAlreadySolved,
    NoAttemptsRemaining,
    GuessChangeNotAllowed,

    // REQ-211 (2026-07-27 fix): the owning game module's live-lookup
    // fallback (Games.LiveLookupUnavailableException) couldn't complete in
    // time — the guess's correctness is genuinely UNKNOWN, not "wrong."
    // Like NeedsDisambiguation, this is not a rejection of anything the
    // player did wrong: nothing about IsCorrect/AttemptCount/Locked is
    // meaningful for it, and — unlike every other Rejected outcome above,
    // which reject before any name-resolution work even starts —
    // SubmitGuessAsync only learns about this partway through name
    // resolution, after IGameModule.ScoreSubmissionAsync throws. Either way,
    // no Guess row is written and no attempt is consumed, so the player gets
    // a genuine retry, not a wasted one.
    LiveLookupUnavailable,

    // S-200/ADR-0098 Consequences: this Guess-based submission path
    // (GuessEndpoints/GuessSubmissionService) is only meant for GameKeys
    // whose owning IGameModule is actually scored through
    // ScoreSubmissionAsync's Guess-attempt shape (today "xg-grid"/
    // "xg-path") — checked against an explicit allow-list supplied by the
    // composition root (ADR-0003: Core never hardcodes a GameKey constant
    // or references a specific game module), rejected before
    // IGameModuleResolver.Resolve/GetMaxAttemptsForCellAsync/
    // ScoreSubmissionAsync are ever called. Closes the risk ADR-0098's
    // Consequences section flagged: xG Predict's REQ-1306 confirm-lock
    // lives only in PredictEndpoints, so a "xg-predict" round reaching
    // XGPredictGameModule.ScoreSubmissionAsync through this path would
    // bypass it. This outcome is unconditional on GetMaxAttemptsForCellAsync's
    // implementation state — it fires before that call is ever made.
    GameNotSupported,
}

public class GuessSubmissionResult
{
    public required GuessSubmissionOutcome Outcome { get; init; }
    public bool IsCorrect { get; init; }
    public int AttemptCount { get; init; }
    public bool Locked { get; init; }

    // Frontend name-display fix: the canonical, properly-cased Player.FullName
    // for a correct guess — never the raw as-typed text (that stays on the
    // Guess row's own SubmittedName, unaffected). Null whenever IsCorrect is
    // false; there is no real player to display a name for.
    public string? ResolvedPlayerName { get; init; }

    // REQ-214: the resolved player's Wikidata photo (Player.PhotoUrl),
    // alongside ResolvedPlayerName — same null-whenever-not-IsCorrect rule,
    // plus null whenever Wikidata has no P18 for this player (never an
    // error either way; the field is simply absent).
    public string? ResolvedPlayerPhotoUrl { get; init; }

    // REQ-216/ADR-0057: the canonical name of a real, but wrong, player a
    // LOCKED, FINAL-incorrect guess turned out to name — the mirror-image
    // case of ResolvedPlayerName above (which is only ever set when
    // IsCorrect). Null whenever IsCorrect is true (nothing to show — REQ-214
    // owns that case), whenever the cell isn't locked yet (state 2 is
    // completely unaffected by this REQ), or whenever the guess string
    // matched no real PlayerNameIndex candidate at all (ADR-0007's "no
    // identity to show" case). Never the raw as-typed guess text — same
    // "misleading" reasoning ResolvedPlayerName's own doc comment gives; see
    // GuessSubmissionService.SubmitGuessAsync for where this is resolved.
    public string? IncorrectGuessMatchedPlayerName { get; init; }

    // ADR-0057: additive alongside IncorrectGuessMatchedPlayerName, same
    // null-whenever-that's-null rule, plus independently null whenever the
    // Wikidata-only lookup timed out, errored, or genuinely found no photo —
    // a silent, graceful fallback (never an error, never fail-closed; there
    // is no correctness verdict left to compute for a guess already known to
    // be wrong).
    public string? IncorrectGuessMatchedPlayerPhotoUrl { get; init; }

    // REQ-209: non-null and non-empty only when Outcome is
    // NeedsDisambiguation — the candidates the player must choose between.
    // Null in every other case.
    public IReadOnlyList<DisambiguationCandidate>? DisambiguationCandidates { get; init; }

    public static GuessSubmissionResult Rejected(GuessSubmissionOutcome outcome) =>
        new() { Outcome = outcome };

    public static GuessSubmissionResult Accepted(
        bool isCorrect, int attemptCount, bool locked, string? resolvedPlayerName = null, string? resolvedPlayerPhotoUrl = null,
        string? incorrectGuessMatchedPlayerName = null, string? incorrectGuessMatchedPlayerPhotoUrl = null) =>
        new()
        {
            Outcome = GuessSubmissionOutcome.Accepted,
            IsCorrect = isCorrect,
            AttemptCount = attemptCount,
            Locked = locked,
            ResolvedPlayerName = resolvedPlayerName,
            ResolvedPlayerPhotoUrl = resolvedPlayerPhotoUrl,
            IncorrectGuessMatchedPlayerName = incorrectGuessMatchedPlayerName,
            IncorrectGuessMatchedPlayerPhotoUrl = incorrectGuessMatchedPlayerPhotoUrl,
        };

    // REQ-209/REQ-210: returned instead of Accepted whenever the game
    // module's ScoreResult carries more than one fitting candidate — the
    // caller (GuessSubmissionService.SubmitGuessAsync) must return this
    // without ever touching guessRepository, so showing the prompt never
    // consumes an attempt.
    public static GuessSubmissionResult NeedsDisambiguation(IReadOnlyList<DisambiguationCandidate> candidates) =>
        new() { Outcome = GuessSubmissionOutcome.NeedsDisambiguation, DisambiguationCandidates = candidates };
}
