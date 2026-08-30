using XGArcade.Core.Games;

namespace XGArcade.Games.XGPredict;

// COMP-15: IGameModule implementation for xG Predict, the third game hosted
// on the platform, alongside Games.XGGrid (COMP-05) and Games.XGPath
// (COMP-11). This is a structural scaffold only — the IGameModule boundary
// and GameKey registration, no real generation/submission/scoring logic.
// See docs/requirements-document.md §4.14 (REQ-1301-1305), ADR-0094
// (API-Football fixtures/results as the data source), and ADR-0095 (xG
// Predict's conventional higher-is-better scoring exception to ADR-0021)
// for the full design this class will eventually implement.
//
// Deliberately holds no dependencies yet (no repository, no DataSync
// client, no constructor parameters) — unlike Games.XGGrid/Games.XGPath's
// own IGameModule implementations at the equivalent point in their history,
// this class doesn't yet have a persisted entity shape to depend on.
// REQ-1301's round/match shape (a fixed set of 5 real-world Premier League
// matches, each with its own scheduled kickoff time, drawn from a gameweek
// and clustered by kickoff, with a whole-round lock at the first kickoff
// rather than a per-cell lock) does not obviously fit either existing
// precedent this codebase has for a game's own generated-instance shape:
// not GridTemplate/GridInstance/GridCell's dynamically-matched, N-answer
// cells (COMP-05), and not PathTemplate/PathInstance/PathPuzzle's single
// fixed target-player-per-puzzle shape (ADR-0045, COMP-11) either — a
// PredictMatch-shaped cell's "correctness" is a three-component score
// prediction graded asynchronously after the fact (REQ-1304/1305), not a
// name-matching problem at all. This was flagged back to the requester
// rather than silently decided here (see this scaffolding session's
// handoff notes) — the follow-up backend story implementing
// GenerateInstanceAsync (REQ-1301/1302) should settle it with its own ADR,
// following ADR-0045's precedent, before this class grows real persistence.
public class XGPredictGameModule : IGameModule
{
    public const string XGPredictGameKey = "xg-predict";

    public string GameKey => XGPredictGameKey;

    // TODO(REQ-1301/1302, ADR-0094): select 5 matches from an upcoming
    // Premier League gameweek (tightest kickoff-time clustering, ADR-0094's
    // API-Football fixtures client) and persist them as this instance's
    // matches. Not implemented — this scaffold only wires up the
    // IGameModule boundary. See this class's own doc comment above for the
    // entity-shape decision that needs to happen first.
    public Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Predict round generation is not yet implemented — see REQ-1301/1302 in " +
            "docs/requirements-document.md §4.14 and ADR-0094, plus XGPredictGameModule's own doc comment " +
            "for the entity-shape decision this needs first.");

    // TODO(REQ-1302/1303/1304): validate and persist a two-integer score
    // prediction for one match, subject to the whole-round lock at the
    // first match's kickoff (REQ-1303), then eventually grade it via
    // REQ-1304's three independent, higher-is-better components
    // (ADR-0095) once REQ-1305's asynchronous grading has a confirmed
    // result. Not implemented.
    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Predict prediction submission/scoring is not yet implemented — see REQ-1302/1303/1304 in " +
            "docs/requirements-document.md §4.14 and ADR-0095 (scoring-direction exception).");

    // TODO(REQ-1301/1302): once a real instance shape exists, return the
    // opaque per-match cell ids for it, the same contract
    // GridGameModule/XGPathGameModule already fulfill. Not implemented —
    // there is no generated instance to read ids from yet.
    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Predict has no generated instance to read match/cell ids from yet — see REQ-1301/1302 in " +
            "docs/requirements-document.md §4.14.");

    // TODO(REQ-1302/1303): xG Predict has no per-match attempt cap the way
    // xG Grid/xG Path do (REQ-1302 explicitly rules one out — a prediction
    // may be resubmitted any number of times before the whole-round lock).
    // Whether this method should therefore return something like
    // int.MaxValue, or whether ADR-0041's per-cell attempt-cap concept
    // doesn't apply to this game at all, is not decided here — flagged for
    // whoever implements REQ-1302/1303, not guessed at in this scaffold.
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Predict's attempt-cap model is not yet decided — REQ-1302 rules out a bounded-guess cap the " +
            "way REQ-210 imposes one on xG Grid/xG Path; see docs/requirements-document.md §4.14.");

    // REQ-215/ADR-0052: xG Predict has no row/col category concept at
    // all — a match prediction is a score guess against one fixed
    // real-world fixture, not two independent category axes a candidate
    // must satisfy. This is a permanent "doesn't apply to this game"
    // case, not a "not yet built" one, so it follows
    // XGPathGameModule.GetCellCategoryTypesAsync's own established
    // NotSupportedException precedent rather than NotImplementedException.
    public Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Predict has no row/col category concept — REQ-215's PlayerSuggestion flow does not apply to xg-predict.");

    // REQ-216: xG Predict has no player-name-guess concept to resolve an
    // identity for — a prediction is a numeric score guess against a fixed
    // match, never a wrong player name. Same unconditional-null precedent
    // XGPathGameModule.ResolveWrongGuessPlayerAsync already established
    // for "not applicable to this game," which GuessSubmissionService's
    // own null-means-no-identity contract already handles safely.
    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WrongGuessPlayerInfo?>(null);
}
