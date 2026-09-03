using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1408 (docs/requirements-document.md §4.15): connector count plus
// accumulated first-attempt-failure penalties, minimum 1. Pure, stateless
// calculation — no repository/DbContext/fake needed, unlike every other
// test file in this project.
public class ConnectScoringServiceTests
{
    private readonly ConnectScoringService _service = new();

    private static ConnectChainStep Step(int position, int attemptNumber, bool isValid, bool closesChain = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Position = position,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = Guid.NewGuid(),
            ClaimedClubName = "Club",
            IsValid = isValid,
            ClosesChain = closesChain,
            SubmittedAt = DateTime.UtcNow,
        };

    // ---- REQ-1408 GWT#2: the 1-connector, zero-penalty minimum case --------

    [Test]
    public void REQ1408_CalculateScore_OneConnectorNoFailures_ReturnsOne()
    {
        var steps = new[] { Step(position: 1, attemptNumber: 1, isValid: true, closesChain: true) };

        var score = _service.CalculateScore(steps);

        Assert.That(score, Is.EqualTo(1));
    }

    // ---- REQ-1408 GWT#1: connector count plus penalty count ----------------

    [Test]
    public void REQ1408_CalculateScore_MultipleConnectorsNoFailures_ReturnsConnectorCount()
    {
        var steps = new[]
        {
            Step(position: 1, attemptNumber: 1, isValid: true),
            Step(position: 2, attemptNumber: 1, isValid: true),
            Step(position: 3, attemptNumber: 1, isValid: true, closesChain: true),
        };

        var score = _service.CalculateScore(steps);

        Assert.That(score, Is.EqualTo(3));
    }

    [Test]
    public void REQ1408_CalculateScore_ConnectorsWithFirstAttemptFailures_AddsOnePenaltyPerFailure()
    {
        var steps = new[]
        {
            // Position 1: failed once, then a successful retry.
            Step(position: 1, attemptNumber: 1, isValid: false),
            Step(position: 1, attemptNumber: 2, isValid: true),
            // Position 2: succeeded first try.
            Step(position: 2, attemptNumber: 1, isValid: true, closesChain: true),
        };

        var score = _service.CalculateScore(steps);

        // 2 connectors (both valid steps) + 1 penalty (the position-1 first
        // failure) = 3. The successful retry itself adds nothing beyond the
        // penalty already counted for the failure it followed.
        Assert.That(score, Is.EqualTo(3));
    }

    [Test]
    public void REQ1408_CalculateScore_FailuresAtDifferentPositions_EachAddsItsOwnPenalty()
    {
        var steps = new[]
        {
            Step(position: 1, attemptNumber: 1, isValid: false),
            Step(position: 1, attemptNumber: 2, isValid: true),
            Step(position: 2, attemptNumber: 1, isValid: false),
            Step(position: 2, attemptNumber: 2, isValid: true, closesChain: true),
        };

        var score = _service.CalculateScore(steps);

        // 2 connectors + 2 penalties (one per position's independent first
        // failure) = 4.
        Assert.That(score, Is.EqualTo(4));
    }

    // A second-attempt (retry) failure is never counted as a penalty by this
    // formula — REQ-1407/1408's own rule is that a second consecutive
    // failure busts the player instead, and a busted player's steps never
    // reach this calculation in practice (ConnectMatchLifecycleService only
    // calls this for a player whose chain actually closed). This test
    // exercises the formula in isolation regardless, to pin down that an
    // AttemptNumber == 2 row is never double-counted as a connector AND a
    // penalty.
    [Test]
    public void REQ1408_CalculateScore_SecondAttemptFailure_IsNeverCountedAsAPenalty()
    {
        var steps = new[]
        {
            Step(position: 1, attemptNumber: 1, isValid: false),
            Step(position: 1, attemptNumber: 2, isValid: false),
        };

        var score = _service.CalculateScore(steps);

        // 0 valid connectors + 1 penalty (only the first-attempt failure) =
        // 1, floored at the minimum rather than 0.
        Assert.That(score, Is.EqualTo(1));
    }

    [Test]
    public void REQ1408_CalculateScore_NoStepsAtAll_ReturnsMinimumOne()
    {
        var score = _service.CalculateScore([]);

        Assert.That(score, Is.EqualTo(1));
    }
}
