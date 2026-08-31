using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.FootballData;

namespace XGArcade.Games.XGPredict;

// COMP-15: IGameModule implementation for xG Predict, the third game hosted
// on the platform, alongside Games.XGGrid (COMP-05) and Games.XGPath
// (COMP-11). S-190/S-191 scaffolded the IGameModule boundary/GameKey
// registration only, flagging the entity-shape decision back rather than
// inventing it (see this class's own doc-comment history). This story
// (REQ-1301/1302/1303) implements that shape per ADR-0096 — see that ADR
// for the full reasoning behind PredictTemplate/PredictInstance/PredictMatch/
// PredictMatchPrediction's shape and the ScoreSubmissionAsync return/
// exception contract this class implements below.
//
// Deliberately NOT wired into InternalRoundEndpoints' gameKey switch,
// GuessSubmissionService, or any RoundSchedulingOptions registration —
// that remains a separate, later story (mirrors ADR-0051's precedent for
// deferred scheduling-config wiring; see ADR-0096 §"For AI agents").
// REQ-1304's IScoringStrategy (Core.Scoring.XGPredictScoringStrategy) is
// now registered (ADR-0095) but, per ADR-0096, this module never writes a
// Guess row, so that strategy's ScoreCorrectGuess is never actually
// reachable from here — see its own doc comment. REQ-1305 (asynchronous
// grading) is a separate, later story not implemented here.
public class XGPredictGameModule(
    IPredictInstanceRepository predictInstanceRepository,
    IFootballDataClient footballDataClient,
    TimeProvider? timeProvider = null) : IGameModule
{
    public const string XGPredictGameKey = "xg-predict";

    // REQ-1303: the round-level lock check needs "now" — same injectable-
    // clock precedent as XGPathGameModule's own _timeProvider field (falls
    // back to the real clock in production, already registered as
    // TimeProvider.System in Program.cs's DI container) so tests can pin
    // "now" deterministically.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string GameKey => XGPredictGameKey;

    // REQ-1301: select exactly template.MatchCount matches from the
    // upcoming gameweek's fixture list, minimizing the kickoff-time span
    // across the selected matches.
    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await predictInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new PredictGenerationException($"PredictTemplate '{config.TemplateId}' not found.");

        var fixtures = await footballDataClient.GetUpcomingGameweekFixturesAsync(cancellationToken);

        // REQ-1301's abort-and-log case — a caller is expected to log this
        // (mirrors GridGenerationException's own doc comment: the throw site
        // itself does not log).
        if (fixtures.Count < template.MatchCount)
        {
            throw new PredictGenerationException(
                $"Not enough upcoming fixtures to build a {template.MatchCount}-match xG Predict instance " +
                $"({fixtures.Count} fixtures available).");
        }

        var selected = SelectTightestKickoffCluster(fixtures, template.MatchCount);

        var instanceId = Guid.NewGuid();
        var instance = new PredictInstance
        {
            Id = instanceId,
            TemplateId = template.Id,
            Matches = selected.Select(fixture => new PredictMatch
            {
                Id = Guid.NewGuid(),
                PredictInstanceId = instanceId,
                ExternalFixtureId = fixture.FixtureId,
                HomeTeamName = fixture.HomeTeamName,
                AwayTeamName = fixture.AwayTeamName,
                KickoffUtc = fixture.KickoffUtc,
            }).ToList(),
        };

        await predictInstanceRepository.AddInstanceAsync(instance, cancellationToken);

        return new GameInstance { Id = instance.Id };
    }

    // ADR-0096 §4: validate and store a two-integer score prediction,
    // subject to REQ-1303's whole-round lock at the first match's kickoff.
    // Returns ScoreResult { IsCorrect = false, PlayerAnswerId = null } on a
    // successful store — see the comment on that line for why IsCorrect =
    // false here does NOT mean "wrong" (a known, deliberate misfit ADR-0096
    // §4 documents and does not resolve; correctness for this game does not
    // exist until REQ-1304/1305's grading runs, a separate, later story).
    public async Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        var predictionSubmission = (PredictionSubmission)submission;

        var instance = await predictInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new PredictScoringException($"PredictInstance '{instanceId}' not found.");

        var match = instance.Matches.FirstOrDefault(m => m.Id == predictionSubmission.CellId)
            ?? throw new PredictScoringException($"Match '{predictionSubmission.CellId}' not found in predict instance '{instanceId}'.");

        // REQ-1302: a missing/non-integer value is already ruled out at the
        // C# type level (PredictionSubmission's int fields) — only a
        // negative value needs an explicit check here. This is an ordinary
        // rejected-submission outcome, not an id-resolution failure, so it
        // throws PredictInvalidSubmissionException rather than
        // PredictScoringException (quality-gate fix, 2026-08-30 — see that
        // type's own doc comment for why conflating the two was a bug).
        if (predictionSubmission.HomeGoals < 0 || predictionSubmission.AwayGoals < 0)
        {
            throw new PredictInvalidSubmissionException(
                $"Prediction for match '{match.Id}' must have non-negative goal counts " +
                $"(got {predictionSubmission.HomeGoals}-{predictionSubmission.AwayGoals}).");
        }

        // REQ-1303: the whole round locks at the EARLIEST of the round's 5
        // matches' own kickoff, regardless of which specific match is being
        // predicted here — never each match's own individual kickoff.
        // Quality-gate fix (2026-08-31): reads PredictInstance.LockInstant
        // (the single shared formula) rather than re-deriving it inline —
        // this call site, GET /predict/current, and POST /predict/confirm
        // had all independently computed the exact same expression.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now >= instance.LockInstant)
        {
            throw new PredictRoundLockedException(
                $"Predict instance '{instanceId}' locked at {instance.LockInstant:o}; submissions are no longer accepted.");
        }

        await predictInstanceRepository.AddOrUpdatePredictionAsync(
            match.Id, userId, predictionSubmission.HomeGoals, predictionSubmission.AwayGoals, now, cancellationToken);

        // ADR-0096 §4: IsCorrect = false here means "accepted, not yet
        // gradable" — NEVER "wrong". Correctness for xG Predict does not
        // exist until REQ-1304/1305's asynchronous grading runs, a separate,
        // later story. Do not treat this value the way Grid/Path readers do.
        return new ScoreResult { IsCorrect = false, PlayerAnswerId = null };
    }

    // ADR-0021-equivalent: round-close's unanswered-cell handling needs
    // every cell id for the instance — same contract GridGameModule./
    // XGPathGameModule.GetCellIdsAsync already fulfill. Nothing calls this
    // yet in production (no wiring in this story, see this class's own doc
    // comment above) but it is a trivial, obviously-needed derivative of
    // ADR-0096's entity shape, so it is implemented for real rather than
    // left throwing.
    public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await predictInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new PredictScoringException($"PredictInstance '{instanceId}' not found.");

        return instance.Matches.Select(m => m.Id).ToList();
    }

    // TODO(REQ-1302/1303): xG Predict has no per-match attempt cap the way
    // xG Grid/xG Path do (REQ-1302 explicitly rules one out — a prediction
    // may be resubmitted any number of times before the whole-round lock).
    // Whether this method should therefore return something like
    // int.MaxValue, or whether ADR-0041's per-cell attempt-cap concept
    // doesn't apply to this game at all, is not decided by ADR-0096 or this
    // story — flagged for whoever wires a real submission endpoint (nothing
    // calls this method yet, since GuessSubmissionService is not wired to
    // "xg-predict" — see this class's own doc comment above).
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Predict's attempt-cap model is not yet decided — REQ-1302 rules out a bounded-guess cap the " +
            "way REQ-210 imposes one on xG Grid/xG Path; see docs/requirements-document.md §4.14.");

    // REQ-215/ADR-0053: xG Predict has no row/col category concept at
    // all — a match prediction is a score guess against one fixed
    // real-world fixture, not two independent category axes a candidate
    // must satisfy. This is a permanent "doesn't apply to this game" case,
    // not a "not yet built" one, so it follows
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

    // REQ-1301: the minimum-span k-subset of a value sequence is always some
    // contiguous window of that sequence once sorted — any subset that
    // "skips over" a smaller value in favor of a larger one can only ever
    // widen its own span, never narrow it — so a single sort + linear
    // sliding window over the sorted fixtures finds the true minimum-span
    // subset without enumerating every C(n, k) combination. First occurrence
    // wins on a tie (`<`, not `<=`, below), matching REQ-1301's
    // determinism requirement.
    private static List<FootballDataFixture> SelectTightestKickoffCluster(
        IReadOnlyList<FootballDataFixture> fixtures, int matchCount)
    {
        var sorted = fixtures.OrderBy(f => f.KickoffUtc).ToList();

        var bestStartIndex = 0;
        var bestSpan = sorted[matchCount - 1].KickoffUtc - sorted[0].KickoffUtc;

        for (var startIndex = 1; startIndex <= sorted.Count - matchCount; startIndex++)
        {
            var span = sorted[startIndex + matchCount - 1].KickoffUtc - sorted[startIndex].KickoffUtc;
            if (span < bestSpan)
            {
                bestSpan = span;
                bestStartIndex = startIndex;
            }
        }

        return sorted.GetRange(bestStartIndex, matchCount);
    }
}
