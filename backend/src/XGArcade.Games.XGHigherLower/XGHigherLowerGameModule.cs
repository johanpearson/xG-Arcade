using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower;

// COMP-18: IGameModule implementation for xG Higher/Lower, a fifth game
// hosted on the platform, alongside Games.XGGrid (COMP-05), Games.XGPath
// (COMP-11), Games.XGPredict (COMP-15), and Games.XGConnect (COMP-17).
// Unlike xG Connect, this game DOES fit the existing Round model — see
// ADR-0110 for the full "why Round, not a new first-class concept like
// ConnectMatch" reasoning, and docs/requirements-document.md §4.16
// (REQ-1501 through REQ-1505) for the full Given/When/Then behavior.
//
// S-224 implemented REQ-1501/1502/1503: GenerateInstanceAsync's real
// eligibility-checked category/baseline/comparator-sequence generation
// algorithm, persisted via IHigherLowerInstanceRepository, plus
// GetCellIdsAsync (a trivial, obviously-needed derivative of the entity
// shape, once it exists — same reasoning XGPredictGameModule.
// GetCellIdsAsync's own doc comment gives for doing this before the game is
// wired into scheduling).
//
// This story (S-225) implements REQ-1504: ScoreSubmissionAsync's real
// correctness/streak-progression/terminal-attempt logic, reading and writing
// the new per-participant HigherLowerAttempt row via
// IHigherLowerInstanceRepository.GetAttemptAsync/SaveAttemptAsync, and wires
// PurgeUserDataAsync to anonymize that new table (REQ-710). See
// HigherLowerAttempt's own doc comment for the entity shape and the
// nullable-UserId/anonymize decision. GetMaxAttemptsForCellAsync remains
// NotImplementedException — see that method's own doc comment for why this
// is now a resolved "doesn't apply the way ADR-0041 assumes" decision, not
// an open question, mirroring XGPredictGameModule.GetMaxAttemptsForCellAsync's
// own still-NotImplementedException case for the identical "no caller yet"
// reason.
//
// Deliberately still NOT wired into RoundSchedulingOptions/
// IRoundSchedulingOptionsResolver/GuessSubmissionAllowedGameKeys/
// InternalRoundEndpoints's gameKey switch — that remains S-226/S-227,
// mirroring ADR-0096's precedent for xG Predict's own staged rollout. This
// game's guess-submission model does not fit GuessSubmissionService's
// per-cell Guess-row shape at all (no submitted player name/disambiguation
// concept — see HigherLowerSubmission's own doc comment), so S-227's
// dedicated endpoints are expected to call ScoreSubmissionAsync directly,
// mirroring PredictEndpoints' shape, not go through GuessSubmissionService.
//
// REQ-1501's numeric-stat-category resolution (ADR to be written separately
// after this story lands — see this class's own history/PR description for
// the resolved design this implements): PlayerAttribute/PlayerOverride
// (COMP-06) today stores only categorical string attributes ("club" |
// "nationality" | "trophy"), never a numeric stat. Rather than a new
// external data source (out of scope, MVP-SCOPE.md), a numeric stat
// category is DERIVED as a COUNT of a player's effective PlayerAttribute
// rows for one AttributeType — "trophy" (trophy count, directly realizing
// REQ-1501's own "league titles won" example) and "club" (career-clubs-
// represented count). "nationality" is permanently excluded as a candidate:
// it is virtually always exactly one value per player, so any two players
// would almost always tie, defeating REQ-1502's whole purpose.
public class XGHigherLowerGameModule(
    IHigherLowerInstanceRepository higherLowerInstanceRepository,
    IPlayerOverrideRepository playerOverrideRepository,
    HigherLowerGenerationOptions options,
    Random? random = null) : IGameModule
{
    public const string XGHigherLowerGameKey = "xg-higher-lower";

    // REQ-1501: the two AttributeTypes whose effective PlayerAttribute row
    // count is a real, meaningful "the more the better" numeric stat — see
    // this class's own doc comment above for why "nationality" is excluded.
    // Order here has no significance (GenerateInstanceAsync always shuffles
    // it) — kept as a private static field purely so it's defined once, not
    // re-allocated per call.
    private static readonly string[] CandidateStatCategories = ["trophy", "club"];

    // Injectable for testability — defaults to Random.Shared in production,
    // same "no DI registration needed for Random itself" precedent
    // GridGenerationService's own _random field establishes.
    private readonly Random _random = random ?? Random.Shared;

    public string GameKey => XGHigherLowerGameKey;

    // REQ-1501/1502/1503/ADR-0110: select exactly one eligible stat category
    // and generate one fixed, fully-ordered baseline-plus-comparator
    // sequence, shared by every participant of the Round.
    //
    // For each candidate category, in shuffled order (so the choice isn't
    // always the same one when multiple are eligible): build the effective-
    // count pool for that category (REQ-1501's "non-null recorded value"
    // eligibility check, done once here, never per participant); if the
    // pool is smaller than the required sequence length (baseline + configured
    // comparator count), this category can never work, so move on without
    // spending any attempts on it. Otherwise, attempt up to
    // HigherLowerGenerationOptions.MaxAttemptsPerCategory times to greedily
    // build a full-length sequence (random baseline, then repeatedly append
    // a random remaining player whose value differs from the current tail's
    // value — REQ-1502's no-exact-tie/no-repeat rules) — if a greedy attempt
    // paints itself into a corner before reaching full length, retry with a
    // fresh shuffle/baseline. If every candidate category exhausts its
    // attempt budget without producing a full-length sequence, generation
    // fails closed (REQ-1502's last Given/When/Then block): never persist a
    // shorter-than-configured sequence.
    public async Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var requiredSequenceLength = options.ComparatorCount + 1; // baseline + comparators

        var shuffledCategories = CandidateStatCategories.ToList();
        Shuffle(shuffledCategories);

        foreach (var category in shuffledCategories)
        {
            var effectiveCounts = await playerOverrideRepository.GetEffectivePlayerCountsByAttributeTypeAsync(
                category, cancellationToken);

            // REQ-1502: this category can't possibly satisfy the required
            // length regardless of how the values are distributed — skip
            // without spending any of the attempt budget on it.
            if (effectiveCounts.Count < requiredSequenceLength)
                continue;

            var pool = effectiveCounts.Select(kv => (PlayerId: kv.Key, Value: kv.Value)).ToList();

            for (var attempt = 0; attempt < options.MaxAttemptsPerCategory; attempt++)
            {
                var sequence = TryBuildSequence(pool, requiredSequenceLength);
                if (sequence is null)
                    continue; // painted into a corner — retry this category with a fresh shuffle

                var instanceId = Guid.NewGuid();
                var baseline = sequence[0];
                var comparators = sequence
                    .Skip(1)
                    .Select((player, index) => new HigherLowerComparator
                    {
                        Id = Guid.NewGuid(),
                        HigherLowerInstanceId = instanceId,
                        SequencePosition = index,
                        PlayerId = player.PlayerId,
                        Value = player.Value,
                    })
                    .ToList();

                var instance = new HigherLowerInstance
                {
                    Id = instanceId,
                    TemplateId = config.TemplateId,
                    StatCategory = category,
                    BaselinePlayerId = baseline.PlayerId,
                    BaselineValue = baseline.Value,
                    Comparators = comparators,
                };

                await higherLowerInstanceRepository.AddInstanceAsync(instance, cancellationToken);

                return new GameInstance { Id = instance.Id };
            }
        }

        // REQ-1502's fail-closed case — a caller is expected to log this
        // (mirrors PredictGenerationException's/GridGenerationException's own
        // doc comment: the throw site itself does not log).
        throw new HigherLowerGenerationException(
            $"Could not build a full-length ({requiredSequenceLength}-player) xG Higher/Lower comparator " +
            $"sequence for any candidate stat category ({string.Join(", ", CandidateStatCategories)}).");
    }

    // REQ-1502: a single greedy attempt at a full-length, no-tie/no-repeat
    // sequence — a random baseline, then repeatedly append a random
    // remaining player whose Value differs from the current tail's Value.
    // Returns null (rather than throwing) if it paints itself into a corner
    // before reaching requiredLength, so the caller can retry with a fresh
    // shuffle. pool is not mutated (a local copy is shuffled instead), so
    // the caller can safely reuse its own pool list across attempts.
    private List<(Guid PlayerId, int Value)>? TryBuildSequence(
        List<(Guid PlayerId, int Value)> pool, int requiredLength)
    {
        var remaining = new List<(Guid PlayerId, int Value)>(pool);
        Shuffle(remaining);

        var sequence = new List<(Guid PlayerId, int Value)> { remaining[0] };
        remaining.RemoveAt(0);

        while (sequence.Count < requiredLength)
        {
            var tailValue = sequence[^1].Value;
            var candidates = remaining.Where(p => p.Value != tailValue).ToList();
            if (candidates.Count == 0)
                return null;

            var next = candidates[_random.Next(candidates.Count)];
            sequence.Add(next);
            remaining.Remove(next);
        }

        return sequence;
    }

    // Fisher-Yates in-place shuffle — same role as GridGenerationService's
    // own Shuffle<T> helper.
    private void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // REQ-1504: compare the current hidden comparator's real value against
    // the participant's current baseline, advance the streak on a correct
    // guess (or end the attempt on an incorrect one, or on reaching the
    // Round's full configured length), reject a guess against an
    // already-ended attempt.
    //
    // No separate "start attempt" call exists (HigherLowerAttempt's own doc
    // comment) — a missing attempt row (GetAttemptAsync returns null) is
    // treated as the implicit "streak 0, current baseline = the instance's
    // own fixed starting baseline, not yet ended" state a participant's
    // first-ever guess always begins from.
    //
    // instance.Comparators is NOT guaranteed pre-sorted by SequencePosition
    // (HigherLowerComparator's own doc comment / this class's own
    // GenerateInstanceAsync — EF does not order an owned collection on
    // read) — sorted explicitly below before indexing into it.
    public async Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        var higherLowerSubmission = (HigherLowerSubmission)submission;

        var instance = await higherLowerInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new HigherLowerScoringException($"HigherLowerInstance '{instanceId}' not found.");

        var attempt = await higherLowerInstanceRepository.GetAttemptAsync(instanceId, userId, cancellationToken);

        var streakLength = attempt?.StreakLength ?? 0;
        var currentBaselinePlayerId = attempt?.CurrentBaselinePlayerId ?? instance.BaselinePlayerId;
        var currentBaselineValue = attempt?.CurrentBaselineValue ?? instance.BaselineValue;
        var hasEnded = attempt?.HasEnded ?? false;

        // REQ-1504's last Given/When/Then block: an ended attempt accepts no
        // further guesses.
        if (hasEnded)
        {
            throw new HigherLowerAttemptEndedException(
                $"HigherLowerAttempt for instance '{instanceId}', user '{userId}' has already ended; " +
                "no further guesses are accepted.");
        }

        var orderedComparators = instance.Comparators.OrderBy(c => c.SequencePosition).ToList();

        // streakLength doubles as the 0-based SequencePosition of the next
        // comparator still to guess (HigherLowerAttempt's own doc comment) —
        // hasEnded being false above guarantees this index is always within
        // bounds (a full-length correct run sets hasEnded = true in the same
        // write that reaches the last position, so a subsequent call would
        // have thrown above instead of reaching this line).
        var currentComparator = orderedComparators[streakLength];

        var isCorrect = higherLowerSubmission.Direction == HigherLowerDirection.Higher
            ? currentComparator.Value > currentBaselineValue
            : currentComparator.Value < currentBaselineValue;

        int newStreakLength;
        Guid newBaselinePlayerId;
        int newBaselineValue;
        bool newHasEnded;

        if (isCorrect)
        {
            // REQ-1504: correct guess — reveal the comparator, increment the
            // streak, the comparator becomes the new baseline. Terminal
            // (newHasEnded = true) exactly when every comparator in the
            // sequence has now been guessed correctly — an attempt can never
            // exceed the Round's configured comparator count.
            newStreakLength = streakLength + 1;
            newBaselinePlayerId = currentComparator.PlayerId;
            newBaselineValue = currentComparator.Value;
            newHasEnded = newStreakLength >= orderedComparators.Count;
        }
        else
        {
            // REQ-1504: incorrect guess — the attempt ends with its streak
            // counted at the length reached BEFORE this guess; the baseline
            // does not advance (there is nothing further to show).
            newStreakLength = streakLength;
            newBaselinePlayerId = currentBaselinePlayerId;
            newBaselineValue = currentBaselineValue;
            newHasEnded = true;
        }

        await higherLowerInstanceRepository.SaveAttemptAsync(
            instanceId, userId, newStreakLength, newBaselinePlayerId, newBaselineValue, newHasEnded, cancellationToken);

        // PlayerAnswerId is always set (unlike xG Grid/xG Path's
        // DisambiguationCandidates-driven null case) — the comparator's
        // identity is revealed on both a correct AND an incorrect guess
        // (REQ-1504), never withheld. DisambiguationCandidates does not
        // apply to this game (no name-guessing concept, see
        // ResolveWrongGuessPlayerAsync's own doc comment below) — left null.
        return new ScoreResult { IsCorrect = isCorrect, PlayerAnswerId = currentComparator.PlayerId };
    }

    // ADR-0021's round-close unanswered-cell handling needs every "cell" id
    // for a generated instance — for xG Higher/Lower that's one id per
    // comparator position in the Round's fixed sequence (mirrors xG
    // Predict's per-match ids). Implemented for real now, same "trivial,
    // obviously-needed derivative" reasoning XGPredictGameModule.
    // GetCellIdsAsync's own doc comment gives — nothing calls this yet in
    // production (S-226/227 wires scheduling), but the entity shape this
    // depends on now exists.
    public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await higherLowerInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new HigherLowerScoringException($"HigherLowerInstance '{instanceId}' not found.");

        return instance.Comparators.Select(c => c.Id).ToList();
    }

    // ADR-0041/REQ-1504 — RESOLVED (S-225, not an open question): this
    // method's per-cell retry-cap concept does not fit xG Higher/Lower's
    // shape at all. A comparator is guessed exactly once ever (a correct
    // guess reveals it and moves on; an incorrect guess reveals it and ends
    // the whole attempt) — there is no "retry the same cell" concept the way
    // REQ-210 imposes one on xG Grid/xG Path. REQ-1504's real cap is a
    // whole-ATTEMPT terminal state (HigherLowerAttempt.HasEnded), enforced
    // directly inside ScoreSubmissionAsync above (the "already-ended attempt
    // rejects any further guess" branch), never through this per-cell
    // method. Left NotImplementedException because nothing calls it yet:
    // GuessSubmissionService is not wired to "xg-higher-lower"
    // (GuessSubmissionAllowedGameKeys deliberately omits it — see this
    // class's own doc comment above) and S-227's dedicated endpoints call
    // ScoreSubmissionAsync directly instead, mirroring
    // XGPredictGameModule.GetMaxAttemptsForCellAsync's own identical
    // still-NotImplementedException case for the same "no caller yet"
    // reason (that class's own doc comment).
    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower has no per-cell attempt cap — REQ-1504's whole-attempt cap is enforced directly " +
            "inside ScoreSubmissionAsync via HigherLowerAttempt.HasEnded, never through this method. Not " +
            "implemented because nothing calls it yet (GuessSubmissionService is not wired to " +
            "\"xg-higher-lower\"); see docs/requirements-document.md §4.16 REQ-1504.");

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

    // REQ-710/S-225: xG Higher/Lower now owns one per-user table —
    // HigherLowerAttempt (this story) — so this is no longer a genuine
    // no-op. Anonymizes (UserId = NULL) rather than hard-deleting, mirroring
    // XGPredictGameModule.PurgeUserDataAsync's call to
    // AnonymizePredictionsByUserIdAsync for PredictMatchPrediction — see
    // HigherLowerAttempt's own doc comment for why the nullable/anonymize
    // shape (not PredictPlayerLock's hard-delete shape) is the right fit
    // here. This is the one place in the codebase allowed to reference
    // IHigherLowerInstanceRepository directly from outside
    // Games.XGHigherLower's own boundary, because this class IS
    // Games.XGHigherLower/COMP-18, not Core (same carve-out
    // XGPredictGameModule.PurgeUserDataAsync's own doc comment documents for
    // IPredictInstanceRepository).
    public async Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await higherLowerInstanceRepository.AnonymizeAttemptsByUserIdAsync(userId, cancellationToken);
}
