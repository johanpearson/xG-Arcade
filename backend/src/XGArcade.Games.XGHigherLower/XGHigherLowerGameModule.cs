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
// This story (S-224) implements REQ-1501/1502/1503: GenerateInstanceAsync's
// real eligibility-checked category/baseline/comparator-sequence generation
// algorithm, persisted via IHigherLowerInstanceRepository. GetCellIdsAsync
// is also implemented for real (a trivial, obviously-needed derivative of
// the entity shape, once it exists — same reasoning XGPredictGameModule.
// GetCellIdsAsync's own doc comment gives for doing this before the game is
// wired into scheduling).
//
// Deliberately NOT implemented here (S-225's job): ScoreSubmissionAsync
// (REQ-1504's guess-submission/streak-progression data model and mechanics)
// and GetMaxAttemptsForCellAsync (REQ-1504's whole-attempt cap — an open
// question per that method's own doc comment). Also deliberately NOT wired
// into RoundSchedulingOptions/IRoundSchedulingOptionsResolver/
// GuessSubmissionAllowedGameKeys/InternalRoundEndpoints's gameKey switch —
// that remains S-226/S-227, mirroring ADR-0096's precedent for xG Predict's
// own staged rollout.
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
    // the current baseline, advance the streak on a correct guess (or end
    // the attempt on an incorrect one, or on reaching the Round's full
    // configured length), reject a guess against an already-ended attempt.
    // Not implemented — the submission/streak-position data model this
    // needs to read and write is undecided; that's S-225's job. Do not
    // guess at this logic; implement against REQ-1504's text.
    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "xG Higher/Lower guess submission is not yet implemented — see docs/requirements-document.md " +
            "§4.16 REQ-1504 for the full correct/incorrect/streak-progression/attempt-capping behavior " +
            "this must implement.");

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

    // ADR-0041: xG Higher/Lower's own attempt-cap model is not decided —
    // REQ-1504 caps an ATTEMPT at the Round's configured comparator count,
    // not a per-cell attempt cap the way REQ-210 imposes one on xG Grid/xG
    // Path (a Higher/Lower guess is a single Higher-or-Lower choice per
    // comparator, not a bounded number of retries against it). Whether this
    // method even applies to this game's shape, or whether REQ-1504's
    // whole-attempt cap is enforced somewhere else entirely (mirroring how
    // xG Predict's own GetMaxAttemptsForCellAsync remains an open question
    // per its own doc comment), is left to whoever implements REQ-1504
    // (S-225). Not implemented.
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
    // all — HigherLowerInstance/HigherLowerComparator (this story) are
    // Round-shared, not per-user, so there is still nothing here for this
    // module to purge. A genuine no-op for now, not a deferred TODO,
    // mirroring GridGameModule's/XGPathGameModule's own identical reasoning
    // for a game whose only per-user data would be Core.Scoring's own Guess
    // row (already anonymized directly by AccountDeletionService before this
    // loop runs). MUST be revisited once REQ-1504's real submission/streak-
    // tracking data model is built (S-225) — if that model introduces its
    // own per-user table (the way xG Predict's PredictMatchPrediction/
    // PredictPlayerLock did), this method needs to anonymize/hard-delete it
    // the same way XGPredictGameModule.PurgeUserDataAsync does. Deliberately
    // NOT left throwing NotImplementedException: this module is registered
    // as a real IGameModule below (COMP-01's AccountDeletionService calls
    // this once per registered module for every deleted user), so throwing
    // here would break account deletion for every user, not just flag a gap.
    public Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
