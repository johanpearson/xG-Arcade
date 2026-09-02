using XGArcade.Core.Games;

namespace XGArcade.Core.Tests.Rounds;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same no-Moq/no-NSubstitute pattern as
// XGArcade.Games.XGGrid.Tests/FakeWikidataLookupService.cs). Lets
// RoundGenerationServiceTests exercise REQ-301's "one round ahead" branching
// without depending on GridGameModule/XGArcade.Games.XGGrid at all — Core
// must never reference a game module directly (ADR-0003).
internal class FakeGameModule(string gameKey) : IGameModule
{
    public string GameKey { get; } = gameKey;

    public int GenerateInstanceAsyncCallCount { get; private set; }

    // ADR-0102: nullable return, matching IGameModule.GenerateInstanceAsync's
    // own signature. Defaults to a non-null instance — the vast majority of
    // existing tests using this fake predate ADR-0102 and never exercise the
    // null ("no new round due") branch; a test proving that branch sets this
    // to a delegate returning null explicitly (see
    // RoundGenerationServiceTests' ADR0102_GenerateNextRoundIfNeeded_GameModuleReturnsNull_*
    // cases).
    public Func<RoundConfig, GameInstance?> GenerateInstanceResult { get; set; } =
        _ => new GameInstance { Id = Guid.NewGuid() };

    public Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        GenerateInstanceAsyncCallCount++;
        return Task.FromResult(GenerateInstanceResult(config));
    }

    // REQ-210's "checked before any name resolution work, not after" ordering
    // requirement (GuessSubmissionServiceTests) is asserted by reading this
    // count after a rejected submission — it must stay zero.
    public int ScoreSubmissionAsyncCallCount { get; private set; }

    public Func<Guid, Guid, object, ScoreResult> ScoreSubmissionResult { get; set; } =
        (_, _, _) => throw new NotImplementedException("Not exercised by round-generation/close tests.");

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        ScoreSubmissionAsyncCallCount++;
        return Task.FromResult(ScoreSubmissionResult(instanceId, userId, submission));
    }

    // ADR-0021: defaults to no cells, since most existing tests using this
    // fake predate the unanswered-cell penalty and don't exercise it —
    // RoundCloseServiceScoringTests sets GetCellIdsResult explicitly where
    // it matters.
    public Func<Guid, IReadOnlyList<Guid>> GetCellIdsResult { get; set; } = _ => [];

    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetCellIdsResult(instanceId));

    // ADR-0041/S-077: defaults to DefaultMaxAttempts, matching
    // GridGameModule's own constant and today's pre-extraction behavior —
    // every existing test that doesn't explicitly override this keeps
    // passing unmodified. Overridable per test via MaxAttemptsForCellResult
    // (e.g. to prove a caller reads this value through IGameModule rather
    // than a hardcoded literal).
    public const int DefaultMaxAttempts = 2;

    public int MaxAttemptsForCellCallCount { get; private set; }

    public Func<Guid, Guid, int> MaxAttemptsForCellResult { get; set; } = (_, _) => DefaultMaxAttempts;

    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default)
    {
        MaxAttemptsForCellCallCount++;
        return Task.FromResult(MaxAttemptsForCellResult(instanceId, cellId));
    }

    // REQ-215/ADR-0052 (S-089, architecture-review fix): not exercised by
    // this fake's existing callers (RoundGenerationService/RoundCloseService
    // tests, which never resolve suggestion category types) — throws by
    // default, same "not exercised by round-generation/close tests" pattern
    // ScoreSubmissionResult's default already uses above, rather than
    // silently returning a fabricated pair of category strings.
    public Func<Guid, Guid, CellCategoryTypes> CellCategoryTypesResult { get; set; } =
        (_, _) => throw new NotImplementedException("Not exercised by round-generation/close tests.");

    public Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        Task.FromResult(CellCategoryTypesResult(instanceId, cellId));

    // REQ-216/ADR-0057: GuessSubmissionServiceTests asserts this fires
    // exactly once (never for state 2/an unlocked incorrect guess, never
    // more than once for the same locked-incorrect cell) by reading this
    // count — defaults to null/never-called, same "not exercised unless a
    // test explicitly configures it" pattern as ScoreSubmissionResult above.
    public int ResolveWrongGuessPlayerAsyncCallCount { get; private set; }

    public Func<Guid, string, WrongGuessPlayerInfo?> ResolveWrongGuessPlayerResult { get; set; } =
        (_, _) => null;

    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default)
    {
        ResolveWrongGuessPlayerAsyncCallCount++;
        return Task.FromResult(ResolveWrongGuessPlayerResult(instanceId, submittedName));
    }

    // REQ-710/S-201: AccountDeletionServiceTests uses this fake for all
    // three registered-module slots (Grid/Path/Predict) to prove
    // DeleteAccountAsync calls PurgeUserDataAsync on every one of them,
    // without pulling XGArcade.Core.Tests into a real Games.XGGrid/
    // Games.XGPath/Games.XGPredict dependency — same "Core must never
    // reference a game module directly" reasoning this file's own
    // top-of-file comment already gives for GenerateInstanceAsync etc.
    // (xG Predict's own real anonymize/hard-delete PurgeUserDataAsync
    // behavior is covered separately, in XGPredictGameModuleTests, where
    // that logic actually lives.) Defaults to a no-op, matching Grid/Path's
    // own real no-op implementation of this method.
    public int PurgeUserDataAsyncCallCount { get; private set; }

    public Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        PurgeUserDataAsyncCallCount++;
        return Task.CompletedTask;
    }
}
