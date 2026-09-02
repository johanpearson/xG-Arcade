namespace XGArcade.Core.Games;

// COMP-03 (Core.Rounds) resolves a Round's GameKey to exactly one
// IGameModule implementation and delegates instance generation/scoring to
// it — Core never references a game-specific type directly. See ADR-0003
// and architecture-document.md boundary rule 2.
public interface IGameModule
{
    string GameKey { get; }

    // ADR-0102: returns null to mean "no new round due for this GameKey
    // right now" — RoundGenerationService treats that exactly like its
    // existing "one round ahead already satisfied" no-op path (returns the
    // existing `latest` Round unchanged, persists nothing new, never an
    // error). A module must only return null when
    // `config.LatestGameInstanceId` was non-null (i.e. it found an
    // existing instance to compare against and determined nothing has
    // changed) — it must never return null for a GameKey's first-ever
    // round. xg-grid/xg-path never return null (they have no equivalent
    // "has the real-world content changed" concept to check) — this is a
    // backward-compatible extension, not a behavior change, for those two.
    Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default);

    Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default);

    // ADR-0021: the full set of cell ids for a generated instance — round
    // close uses this to find, for each round participant, any cell they
    // never submitted a guess for, so it can be penalized the same as an
    // incorrect guess rather than silently scoring 0 (the best possible
    // score under the lowest-wins model). Core.Scoring resolves this only
    // through IGameModule, never by reaching into a game-specific instance
    // table directly (ADR-0003).
    Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default);

    // ADR-0041: a given cell's own max-attempts value, replacing the old
    // single `GuessRules.MaxAttemptsPerCell` global constant. Not every game
    // has a fixed, uniform attempt cap (e.g. xG Path's cap varies
    // target-player to target-player within the same round), so this is
    // resolved per cell through the owning module rather than assumed
    // platform-wide. GuessSubmissionService (REQ-210's lock/cap check),
    // LiveRoundContributionService, and RoundEndpoints all call this instead
    // of reading a shared constant. xG Grid's implementation returns a fixed
    // 2 for every cell, unconditionally — REQ-210's existing behavior,
    // unchanged, now reported through this method instead of the deleted
    // GuessRules.MaxAttemptsPerCell.
    Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default);

    // REQ-215/ADR-0052 (S-089, architecture-review fix): a specific cell's
    // authoritative row/col category types — XGArcade.Api.Suggestions.
    // SuggestionEndpoints' only path to this data, resolved through the
    // owning module rather than a direct IGridInstanceRepository/GridCell
    // read from the Api layer, which is what the original S-089 commit did
    // (a boundary violation flagged by architecture-reviewer — see that
    // review for the full "every other business-logic path resolves cell
    // data through IGameModule" precedent this closes the gap on).
    // Denormalized onto the persisted PlayerSuggestion row at submission
    // time — see that entity's own doc comment.
    //
    // Not every game has a "row/col category" concept at all: xG Path's
    // PathPuzzle has a single fixed TargetPlayerId, not two independent
    // category axes (see XGPathGameModule.ScoreSubmissionAsync's own doc
    // comment) — its implementation of this method throws
    // NotSupportedException rather than fabricating a value, the same
    // "flag it, don't silently guess" discipline this interface's other
    // per-game judgment calls already follow. Throws a
    // GameEntityNotFoundException-derived exception (matching
    // ScoreSubmissionAsync/GetCellIdsAsync's existing convention) when
    // instanceId/cellId don't resolve to a real cell.
    Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default);

    // REQ-216/ADR-0057: resolves the guessed player's canonical name (and,
    // independently, an optional photo) for a cell that has just locked with
    // its final guess still incorrect — called by GuessSubmissionService
    // exactly once, only when it has already determined
    // `locked && !scoreResult.IsCorrect` for THIS submission (never for
    // state 2, an incorrect guess with attempts still remaining, and never
    // more than once per cell — the existing REQ-210 lock/cap rejection
    // checks in GuessSubmissionService already make a second call for the
    // same cell impossible, since a locked cell rejects any further guess
    // before this method would ever be reached again).
    //
    // Returns null when submittedName didn't match any real PlayerNameIndex
    // candidate at all (ADR-0007) — REQ-216's "no identity to show" case,
    // unchanged from today's behavior. A non-null result's PhotoUrl is
    // itself independently nullable — see WrongGuessPlayerInfo's own doc
    // comment for why a resolved name with no photo is a normal, silent
    // outcome (ADR-0057), never an error and never fed into any
    // fail-closed/incorrect verdict (there is none left to compute for a
    // guess already known to be wrong).
    //
    // This is a SEPARATE, lower-priority trigger from REQ-211's existing
    // guess-time live lookup (IGameModule.ScoreSubmissionAsync's own
    // fallback path) — Wikidata-only, never API-Football, never gated on
    // any ExternalApiUsage threshold (ADR-0057). Not every game supports
    // this display feature: xG Path's implementation returns null
    // unconditionally rather than fabricating a value — see that class's
    // own comment.
    Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default);

    // REQ-710/S-201 (quality-gate fix): AccountDeletionService (Core.Auth,
    // COMP-01) calls this once per registered IGameModule for every deleted
    // user, so each game module can anonymize/hard-delete whatever per-user
    // data IT owns — mirroring this interface's existing "every method is
    // resolved through the owning module, Core never reaches into
    // game-specific storage directly" discipline (ADR-0003). Replaces an
    // earlier version of this story that had AccountDeletionService take a
    // direct constructor dependency on IPredictInstanceRepository
    // (Games.XGPredict/COMP-15's own persistence) — the exact anti-pattern
    // IRoundScoreSource's own doc comment (Core.Scoring) already calls out
    // by name as something Core must never do.
    //
    // Not every game has per-user data of its own to purge: xG Grid/xG
    // Path's only per-user table is Guess, which is Core.Scoring's OWN
    // entity (COMP-04) and is already anonymized directly by
    // AccountDeletionService via IGuessRepository before this loop runs —
    // both modules' implementations are a genuine no-op. A game that DOES
    // own per-user storage (xG Predict's PredictMatchPrediction/
    // PredictPlayerLock) anonymizes/hard-deletes it here instead, exactly
    // the way it would decide to anonymize vs. hard-delete for its own
    // schema — Core never needs to know which strategy applies to which
    // game's tables.
    Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default);
}
