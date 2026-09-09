using XGArcade.Core.Games;

namespace XGArcade.Games.XGHigherLower;

// COMP-18: IGameModule implementation for xG Higher/Lower, a fifth game
// hosted on the platform, alongside Games.XGGrid (COMP-05), Games.XGPath
// (COMP-11), Games.XGPredict (COMP-15), and Games.XGConnect (COMP-17).
// Unlike xG Connect, this game DOES fit the existing Round model — see
// ADR-0110 for the full "why Round, not a new first-class concept like
// ConnectMatch" reasoning, and docs/requirements-document.md §4.16
// (REQ-1501 through REQ-1505) for the full Given/When/Then behavior this
// class must eventually implement:
//
//   REQ-1501: stat-category and player-value eligibility.
//   REQ-1502: comparator eligibility (no exact ties, no repeated player),
//             checked at Round-generation time, generation fails closed
//             if a full-length sequence can't be built.
//   REQ-1503: Round generation — one fixed stat category plus one fixed,
//             fully-ordered comparator sequence (baseline + a configured
//             comparator count, defaulting to 10), generated once and
//             shared by every participant.
//   REQ-1504: guess submission and streak progression, capped at the
//             Round's configured comparator count.
//   REQ-1505: FinalPoints = streak length reached, ranked via the standard
//             Global/custom-league leaderboards like every other GameKey.
//
// This is a structural scaffold only — GenerateInstanceAsync and
// ScoreSubmissionAsync throw NotImplementedException rather than silently
// returning fake data; nothing about the comparator-sequence generation
// algorithm, the guess-submission/streak-tracking data model, or which
// stat categories are actually eligible has been decided or built yet.
// Do not fill these in with guessed behavior — implement against
// REQ-1501-1505's text once that story is picked up.
//
// No xG-Higher/Lower-specific persisted entity (an equivalent of
// GridInstance/PathInstance/PredictInstance) exists yet either — that
// shape (how a Round's fixed category/baseline/comparator sequence and a
// participant's in-progress streak position are stored) is itself part of
// the not-yet-decided implementation, not assumed by this scaffold.
public class XGHigherLowerGameModule : IGameModule
{
    public const string XGHigherLowerGameKey = "xg-higher-lower";

    public string GameKey => XGHigherLowerGameKey;

    // REQ-1501/1502/1503/ADR-0110: select exactly one eligible stat category
    // and generate one fixed, fully-ordered baseline-plus-comparator
    // sequence, shared by every participant of the Round. Must fail closed
    // (never persist a shorter-than-configured sequence) per REQ-1502's
    // last Given/When/Then block. Not implemented — the eligibility checks,
    // sequence-generation algorithm, and the entity shape a generated
    // instance is persisted as are all undecided. Do not guess at this
    // logic; implement against REQ-1501/1502/1503's text.
    public Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower Round generation is not yet implemented — see docs/requirements-document.md " +
            "§4.16 REQ-1501/1502/1503 and ADR-0110 for the full category-selection/comparator-sequence " +
            "eligibility and generation-time fail-closed behavior this must implement.");

    // REQ-1504: compare the current hidden comparator's real value against
    // the current baseline, advance the streak on a correct guess (or end
    // the attempt on an incorrect one, or on reaching the Round's full
    // configured length), reject a guess against an already-ended attempt.
    // Not implemented — the submission/streak-position data model this
    // needs to read and write is undecided (see this class's own doc
    // comment above). Do not guess at this logic; implement against
    // REQ-1504's text.
    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower guess submission is not yet implemented — see docs/requirements-document.md " +
            "§4.16 REQ-1504 for the full correct/incorrect/streak-progression/attempt-capping behavior " +
            "this must implement.");

    // ADR-0021: Core.Scoring's round-close unanswered-cell handling needs
    // every "cell" id for a generated instance — for xG Higher/Lower this
    // most likely maps to one id per comparator position in the Round's
    // fixed sequence (mirroring xG Predict's per-match ids), but that is
    // not decided here, since it depends on GenerateInstanceAsync's own
    // not-yet-built entity shape. Not implemented.
    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower has no persisted instance/comparator-position schema yet — see " +
            "docs/requirements-document.md §4.16 REQ-1503 for the sequence shape this must resolve ids " +
            "against once GenerateInstanceAsync is implemented.");

    // ADR-0041: xG Higher/Lower's own attempt-cap model is not decided —
    // REQ-1504 caps an ATTEMPT at the Round's configured comparator count,
    // not a per-cell attempt cap the way REQ-210 imposes one on xG Grid/xG
    // Path (a Higher/Lower guess is a single Higher-or-Lower choice per
    // comparator, not a bounded number of retries against it). Whether this
    // method even applies to this game's shape, or whether REQ-1504's
    // whole-attempt cap is enforced somewhere else entirely (mirroring how
    // xG Predict's own GetMaxAttemptsForCellAsync remains an open question
    // per its own doc comment), is left to whoever implements REQ-1504. Not
    // implemented.
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower's attempt-cap model is not yet decided — see docs/requirements-document.md " +
            "§4.16 REQ-1504 for the Round-level (not per-cell) streak-capping behavior; whether this method " +
            "applies to this game's shape at all is an open question for whoever implements it.");

    // REQ-215/ADR-0053: xG Higher/Lower has no row/col category concept at
    // all — a comparison is a single numeric stat value against one fixed
    // active category, never two independent category axes a candidate
    // must satisfy. This is a permanent "doesn't apply to this game" case,
    // not a "not yet built" one, mirroring XGPathGameModule's/
    // XGPredictGameModule's own established NotSupportedException
    // precedent for the same method.
    public Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Higher/Lower has no row/col category concept — REQ-215's PlayerSuggestion flow does not " +
            "apply to xg-higher-lower.");

    // REQ-216: xG Higher/Lower has no player-name-guess concept at all — the
    // player picks Higher or Lower, never types a player's name (see
    // docs/requirements-document.md §4.16's own "Why this game" note: no
    // autocomplete/name-matching surface exists for this game). Same
    // unconditional-null precedent XGPathGameModule/XGPredictGameModule
    // already established for "not applicable to this game."
    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WrongGuessPlayerInfo?>(null);

    // REQ-710: xG Higher/Lower currently owns no per-user persisted data at
    // all — no instance/attempt entity has been built yet (see this class's
    // own doc comment above), so there is nothing here for this module to
    // purge yet. A genuine no-op for now, not a deferred TODO, mirroring
    // GridGameModule's/XGPathGameModule's own identical reasoning for a
    // game whose only per-user data would be Core.Scoring's own Guess row
    // (already anonymized directly by AccountDeletionService before this
    // loop runs). MUST be revisited once REQ-1504's real submission/streak-
    // tracking data model is built — if that model introduces its own
    // per-user table (the way xG Predict's PredictMatchPrediction/
    // PredictPlayerLock did), this method needs to anonymize/hard-delete it
    // the same way XGPredictGameModule.PurgeUserDataAsync does. Deliberately
    // NOT left throwing NotImplementedException: this module is registered
    // as a real IGameModule below (COMP-01's AccountDeletionService calls
    // this once per registered module for every deleted user), so throwing
    // here would break account deletion for every user, not just flag a gap.
    public Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
