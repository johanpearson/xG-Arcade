using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGGrid;

// COMP-05: IGameModule implementation for the xG Grid game.
//
// S-119 (pure refactor, no behavior change): this class is now a thin
// IGameModule adapter — grid generation (IGridGenerationService), name
// matching/disambiguation (IGridNameMatcher), and live-lookup dispatch
// (IGridLiveLookupDispatcher) were split into their own classes, following
// the same convention docs/decisions/0067-player-store-repository-split.md
// established: no facade, each new interface independently registered. See
// each interface's own doc comment for its slice of the original
// GridGameModule; see this class's own doc comment on each remaining
// method for why it stayed here.
public class GridGameModule(
    IGridInstanceRepository gridInstanceRepository,
    IPlayerNameIndexRepository playerNameIndexRepository,
    IGridGenerationService generationService,
    IGridNameMatcher nameMatcher,
    IGridLiveLookupDispatcher liveLookupDispatcher,
    GridLiveLookupOptions liveLookupOptions) : IGameModule
{
    public const string XGGridGameKey = "xg-grid";

    // ADR-0041: REQ-210's per-cell attempt cap for xG Grid — every cell gets
    // the same fixed allowance, unconditionally. See GetMaxAttemptsForCellAsync.
    // A fixed grid-wide value with no other owner, so it stays here rather
    // than moving into any of the three split-out classes.
    private const int MaxAttemptsPerCell = 2;

    public string GameKey => XGGridGameKey;

    // ADR-0102: never returns null — xg-grid has no real-world-content
    // concept to check against config.LatestGameInstanceId, so it always
    // generates a fresh instance, same as before that interface change.
    public Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default) =>
        generationService.GenerateInstanceAsync(config, cancellationToken);

    // S-009: REQ-210's lock/attempt-cap checks and REQ-202's guess-change
    // policy already happened in Core.Scoring before this was ever called
    // (GuessSubmissionService) — everything here is REQ-207/208/209/211's
    // name-resolution work, delegated to IGridNameMatcher/
    // IGridLiveLookupDispatcher below. This method itself stays on the
    // adapter (rather than moving into either of those classes) because it
    // owns the *orchestration* between them — the instance/cell lookup, and
    // the gate/retry sequencing — not any name-matching or live-lookup logic
    // of its own.
    public async Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        var guessSubmission = (GuessSubmission)submission;

        var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");

        var cell = instance.Cells.FirstOrDefault(c => c.Id == guessSubmission.CellId)
            ?? throw new GuessScoringException($"Cell '{guessSubmission.CellId}' not found in grid instance '{instanceId}'.");

        // REQ-208: normalize once — FindMatchAsync below applies the
        // normalized/alias/fuzzy comparisons in order (exact primary name,
        // then alias, then bounded fuzzy).
        var normalized = PlayerNameNormalizer.Normalize(guessSubmission.SubmittedName);

        var result = await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);

        // REQ-209: a genuinely correct guess never needs a live-lookup
        // retry; neither does an ambiguous one — the cell already resolved
        // from cache (just to more than one fitting candidate), which is a
        // different case from "didn't already resolve from cache" below.
        if (result.IsCorrect || (result.DisambiguationCandidates?.Count ?? 0) > 0)
            return result;

        // REQ-211 (2026-07-27 fix): grid generation's cached match count
        // (REQ-101/MinValidAnswers) only ever needed to prove this cell had
        // *some* valid answers, never to catalog every one, so a guess can
        // be genuinely correct even though nothing cached confirms it yet —
        // either because this exact player was never synced at all, or
        // because they already exist with one category's attribute cached
        // (from an unrelated cell) but not this cell's other one. Re-running
        // this cell's own country x club intersection query is an upsert,
        // not a fresh insert (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync,
        // via WikidataLookupService.PersistMatchesAsync), so one call fixes
        // both cases and completes the cell's whole answer key for later
        // guesses too, not just this one name.
        //
        // Gated on PlayerNameIndex first (REQ-207/S-032 built this, 2026-07-17
        // — the "Tier 1, not built" gap this comment used to describe is
        // closed): only a guess that matched a real PlayerNameIndex candidate
        // is worth a live Wikidata round-trip — a name that matched nothing
        // there at all can never be a real player, so paying for a live
        // lookup (and the retry latency that comes with it, this bug
        // bundle's original report) on every wrong guess was pure waste.
        // Every other trigger condition is unchanged: bounded by REQ-210's
        // 2-attempt cap, same as every other guess-time cost, and still a
        // single retry, never a loop.
        // ADR-0070/S-128: an operational kill switch for this fallback only —
        // grid generation's own live lookup (REQ-103, GetMatchCountAsync) is
        // a separate call path through the same IGridLiveLookupDispatcher and
        // is deliberately untouched by this flag. When disabled, this must
        // produce exactly the outcome an unresolved guess had before REQ-211
        // existed at all: fail closed, `result` unchanged, no PlayerNameIndex
        // query or live-lookup dispatch spent on it either.
        if (!liveLookupOptions.Enabled)
            return result;

        if (!await playerNameIndexRepository.ExistsByNormalizedNameAsync(normalized, cancellationToken))
            return result;

        if (!await liveLookupDispatcher.TryRefreshCellAsync(cell, cancellationToken))
            return result;

        return await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
    }

    // ADR-0021: round-close's unanswered-cell penalty needs every cell id
    // for the instance, regardless of whether anyone ever guessed it. A
    // trivial IGridInstanceRepository passthrough, not a generation/
    // matching/live-lookup concern — stays on the adapter.
    public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");

        return instance.Cells.Select(c => c.Id).ToList();
    }

    // ADR-0041: REQ-210's existing "2 guesses per cell" behavior, now
    // reported through IGameModule instead of the deleted
    // GuessRules.MaxAttemptsPerCell. Every xG Grid cell shares the same
    // fixed allowance — no repository lookup, no branching on instanceId or
    // cellId — deliberately identical to today's behavior, per ADR-0041's
    // "pure extraction, not a rule change" mandate.
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        Task.FromResult(MaxAttemptsPerCell);

    // REQ-215/ADR-0052 (S-089, architecture-review fix): SuggestionEndpoints'
    // only path to a cell's row/col category types — the exact
    // IGridInstanceRepository.GetCellByIdAsync call that endpoint used to
    // make directly, now behind the IGameModule boundary (ADR-0003). A
    // trivial passthrough, same reasoning as GetCellIdsAsync above.
    // instanceId is accepted (matching every other IGameModule method's
    // shape) but unused, same as GetMaxAttemptsForCellAsync above —
    // GridCell.Id is already globally unique (GetCellByIdAsync's own doc
    // comment), so no instance-scoping lookup is needed to resolve it.
    // Deliberately no check that cellId belongs to instanceId either —
    // preserves the original endpoint's documented "no further validation
    // of roundId/cellId's relationship" behavior unchanged; only the
    // *caller* of this data moved, not what it validates.
    public async Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default)
    {
        var cell = await gridInstanceRepository.GetCellByIdAsync(cellId, cancellationToken)
            ?? throw new GuessScoringException($"Cell '{cellId}' not found.");

        return new CellCategoryTypes(cell.RowCategoryType, cell.ColCategoryType);
    }

    // REQ-216/ADR-0057: called by GuessSubmissionService exactly once, only
    // once it has already determined a cell just locked with its final
    // guess still incorrect — see IGameModule.ResolveWrongGuessPlayerAsync's
    // own doc comment for the full "when/how often" contract this method
    // relies on its caller enforcing. instanceId is kept in this method's
    // own signature only because IGameModule requires it — it was never
    // referenced in the original implementation, so it isn't forwarded to
    // IGridNameMatcher.ResolveWrongGuessPlayerAsync, which drops it (see
    // that interface's own doc comment).
    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
        nameMatcher.ResolveWrongGuessPlayerAsync(submittedName, cancellationToken);

    // REQ-710/S-201: xG Grid's only per-user table is Guess, which is
    // Core.Scoring's OWN entity (COMP-04) — AccountDeletionService already
    // anonymizes it directly via IGuessRepository before ever reaching this
    // loop (see IGameModule.PurgeUserDataAsync's own doc comment). xG Grid
    // itself (GridInstance/GridCell) owns no per-user row at all, so there is
    // nothing left here for this module to purge — a genuine no-op, not a
    // deferred TODO.
    public Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
