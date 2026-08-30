using Microsoft.Extensions.Logging;
using XGArcade.Core.Scoring;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.ApiFootball;

namespace XGArcade.Games.XGPredict;

// REQ-1305/ADR-0097: see IPredictGradingService's own doc comment for the
// full boundary reasoning (why this lives here, not Core.Scoring).
// Depends on IPredictInstanceRepository, IApiFootballClient,
// XGPredictScoringStrategy (the CONCRETE class, per ADR-0097 Decision
// §2/its Alternatives table — not IScoringStrategy/
// IScoringStrategyResolver), and TimeProvider — no Round/
// IRoundRepository dependency at all (ADR-0097's own kickoff-implies-lock
// simplification; see GetMatchesReadyForGradingAsync's own doc comment).
public class PredictGradingService(
    IPredictInstanceRepository predictInstanceRepository,
    IApiFootballClient apiFootballClient,
    XGPredictScoringStrategy scoringStrategy,
    PredictGradingOptions gradingOptions,
    TimeProvider timeProvider,
    ILogger<PredictGradingService> logger) : IPredictGradingService
{
    public async Task<PredictGradingRunResult> GradeReadyMatchesAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var readyMatches = await predictInstanceRepository.GetMatchesReadyForGradingAsync(
            gradingOptions.TypicalMatchDuration, nowUtc, cancellationToken);

        var graded = 0;
        var voided = 0;
        var stillPending = 0;
        var failed = 0;

        foreach (var match in readyMatches)
        {
            try
            {
                var fixtureResult = await apiFootballClient.GetFixtureResultAsync(match.ExternalFixtureId, cancellationToken);

                switch (fixtureResult.Outcome)
                {
                    case ApiFootballFixtureOutcome.Finished:
                        // ApiFootballFixtureResult's own doc comment
                        // guarantees HomeGoals/AwayGoals are non-null once
                        // Outcome == Finished — trusted here, no
                        // null-check dance (per this story's own
                        // instruction).
                        var actualHomeGoals = fixtureResult.HomeGoals!.Value;
                        var actualAwayGoals = fixtureResult.AwayGoals!.Value;

                        var predictions = await predictInstanceRepository.GetPredictionsForMatchAsync(match.Id, cancellationToken);
                        var finalPointsByPredictionId = predictions.ToDictionary(
                            prediction => prediction.Id,
                            prediction => scoringStrategy
                                .ScorePrediction(prediction.HomeGoals, prediction.AwayGoals, actualHomeGoals, actualAwayGoals)
                                .FinalPoints);

                        await predictInstanceRepository.GradeMatchAsync(
                            match.Id, actualHomeGoals, actualAwayGoals, finalPointsByPredictionId, cancellationToken);
                        graded++;
                        break;

                    case ApiFootballFixtureOutcome.PostponedOrAbandoned:
                        await predictInstanceRepository.VoidMatchAsync(match.Id, cancellationToken);
                        voided++;
                        break;

                    // NotYetConfirmed (and any future/default case): no
                    // write at all — the match stays Pending, retried on
                    // the next run (ADR-0097 Decision §3). The job's own
                    // hourly cadence IS the retry loop; no separate
                    // retry-count/backoff state is kept.
                    default:
                        stillPending++;
                        break;
                }
            }
            catch (ApiFootballClientException ex)
            {
                // ADR-0097 Decision §3's last bullet: one match's failure
                // must not abort grading for the round's other matches, or
                // other rounds' matches, in the same run — caught and
                // logged per-match, mirroring InternalRoundEndpoints' own
                // per-failure-mode catch discipline.
                logger.LogError(
                    ex,
                    "xG Predict grading failed for PredictMatch {PredictMatchId} (ExternalFixtureId {ExternalFixtureId}); left Pending, will retry next run.",
                    match.Id, match.ExternalFixtureId);
                failed++;
            }
        }

        return new PredictGradingRunResult(graded, voided, stillPending, failed);
    }
}
