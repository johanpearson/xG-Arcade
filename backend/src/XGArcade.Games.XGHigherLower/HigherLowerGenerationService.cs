using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower;

// Delegation-pattern refactor (2026-09-10, no behavior/REQ change): split
// out of XGHigherLowerGameModule, mirroring GridGameModule's own S-119 split
// into GridGenerationService — see NOTES.md's 2026-09-10 entry for the
// quality-architect finding this closes (raised during S-229's close-out)
// and XGHigherLowerGameModule's own doc comment for why the rest of that
// class (ScoreSubmissionAsync etc.) stayed inline rather than following
// suit — xG Higher/Lower has no separate scoring service to mirror, the
// same reason GridGameModule kept its own ScoreSubmissionAsync inline.
//
// REQ-1501/1502/1503/ADR-0110: select exactly one eligible stat category and
// generate one fixed, fully-ordered baseline-plus-comparator sequence,
// shared by every participant of the Round.
//
// For each candidate category, in shuffled order (so the choice isn't always
// the same one when multiple are eligible): build the effective-count pool
// for that category (REQ-1501's "non-null recorded value" eligibility check,
// done once here, never per participant); if the pool is smaller than the
// required sequence length (baseline + configured comparator count), this
// category can never work, so move on without spending any attempts on it.
// Otherwise, attempt up to HigherLowerGenerationOptions.MaxAttemptsPerCategory
// times to greedily build a full-length sequence (random baseline, then
// repeatedly append a random remaining player whose value differs from the
// current tail's value — REQ-1502's no-exact-tie/no-repeat rules) — if a
// greedy attempt paints itself into a corner before reaching full length,
// retry with a fresh shuffle/baseline. If every candidate category exhausts
// its attempt budget without producing a full-length sequence, generation
// fails closed (REQ-1502's last Given/When/Then block): never persist a
// shorter-than-configured sequence.
//
// REQ-1501's numeric-stat-category resolution (ADR-0111, extended by
// ADR-0112 for S-231): PlayerAttribute/PlayerOverride (COMP-06) never
// stores a new external data source (out of scope, MVP-SCOPE.md) — a
// numeric stat category is DERIVED one of two ways, dispatched by
// GetEffectivePlayerValuesForCategoryAsync below: a COUNT of a player's
// effective PlayerAttribute rows for one AttributeType (ADR-0111 — "trophy"
// only, now that "club" is removed per direct product-owner feedback, S-231),
// or a single recorded value (ADR-0112 — "international-caps"/
// "international-goals", each at most one row per player). "nationality" is
// permanently excluded as a candidate: it is virtually always exactly one
// value per player, so any two players would almost always tie, defeating
// REQ-1502's whole purpose. "club" was removed in S-231 (ADR-0111's own
// Follow-up section pre-approved this exact rollback) — direct user feedback
// that "club count" reads as a boring stat, not an "accomplishment" one the
// way trophy/caps/goals do.
public class HigherLowerGenerationService(
    IHigherLowerInstanceRepository higherLowerInstanceRepository,
    IPlayerOverrideRepository playerOverrideRepository,
    HigherLowerGenerationOptions options,
    Random? random = null) : IHigherLowerGenerationService
{
    // REQ-1501/S-231: the three AttributeTypes offered as candidate
    // categories — see this class's own doc comment above for why
    // "nationality"/"club" are excluded and how each is derived (count vs.
    // single-value, dispatched by GetEffectivePlayerValuesForCategoryAsync).
    // Order here has no significance (GenerateInstanceAsync always shuffles
    // it) — kept as a private static field purely so it's defined once, not
    // re-allocated per call.
    private static readonly string[] CandidateStatCategories = ["trophy", "international-caps", "international-goals"];

    // REQ-1506 (S-231, ADR-0112): the exact AttributeType this floor is
    // read from — always "international-caps", regardless of which category
    // is active (even when the active category IS this one).
    private const string InternationalCapsAttributeType = "international-caps";

    // Injectable for testability — defaults to Random.Shared in production,
    // same "no DI registration needed for Random itself" precedent
    // GridGenerationService's own _random field establishes.
    private readonly Random _random = random ?? Random.Shared;

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var requiredSequenceLength = options.ComparatorCount + 1; // baseline + comparators

        // REQ-1506: the pool-wide caps>=10 floor, evaluated ONCE here (same
        // "evaluated once, never per participant" timing REQ-1501's own
        // eligibility checks already use) and applied to every candidate
        // category's pool below via eligiblePlayerIds.Contains — regardless
        // of which category ends up active, including when the active
        // category is itself "international-caps" or "international-goals".
        // A player with an absent caps value (never returned by
        // GetEffectivePlayerValuesByAttributeTypeAsync at all) never
        // satisfies this floor, the same "absent is never treated as
        // satisfying it" rule REQ-1506's own Given/When/Then establishes.
        var internationalCapsByPlayerId = await playerOverrideRepository.GetEffectivePlayerValuesByAttributeTypeAsync(
            InternationalCapsAttributeType, cancellationToken);
        var eligibleByCapsFloor = internationalCapsByPlayerId
            .Where(kv => kv.Value >= options.MinimumInternationalCaps)
            .Select(kv => kv.Key)
            .ToHashSet();

        var shuffledCategories = CandidateStatCategories.ToList();
        Shuffle(shuffledCategories);

        foreach (var category in shuffledCategories)
        {
            var effectiveValues = await GetEffectivePlayerValuesForCategoryAsync(category, cancellationToken);

            // REQ-1506: intersect with the caps>=10 floor above BEFORE the
            // REQ-1502 length check below — a player who has a non-null
            // active-category value (REQ-1501) but fails the floor must
            // never be selected, applied in addition to, never instead of,
            // REQ-1501's own non-null-value rule.
            var eligibleValues = effectiveValues
                .Where(kv => eligibleByCapsFloor.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // REQ-1502: this category can't possibly satisfy the required
            // length regardless of how the values are distributed — skip
            // without spending any of the attempt budget on it.
            if (eligibleValues.Count < requiredSequenceLength)
                continue;

            var pool = eligibleValues.Select(kv => (PlayerId: kv.Key, Value: kv.Value)).ToList();

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

    // REQ-1501/S-231/ADR-0112: the small, explicit per-category
    // value-derivation dispatch this class's own doc comment describes —
    // deliberately kept this simple (a switch over two known shapes) rather
    // than a plugin/strategy abstraction for what is currently exactly two
    // derivation functions (ADR-0112's own "smallest change that fits" call).
    // Throws for any AttributeType not in CandidateStatCategories — should
    // never happen given this is only ever called with a value from that
    // same array, but fails loudly rather than silently returning an empty
    // pool if a future edit to CandidateStatCategories forgets to extend
    // this dispatch too.
    private Task<IReadOnlyDictionary<Guid, int>> GetEffectivePlayerValuesForCategoryAsync(
        string category, CancellationToken cancellationToken) => category switch
    {
        "trophy" => playerOverrideRepository.GetEffectivePlayerCountsByAttributeTypeAsync(category, cancellationToken),
        "international-caps" or "international-goals" =>
            playerOverrideRepository.GetEffectivePlayerValuesByAttributeTypeAsync(category, cancellationToken),
        _ => throw new InvalidOperationException(
            $"xG Higher/Lower stat category '{category}' has no value-derivation dispatch registered — " +
            "extend GetEffectivePlayerValuesForCategoryAsync alongside any change to CandidateStatCategories."),
    };
}
