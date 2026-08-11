using Microsoft.Extensions.Logging;
using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of GridGameModule.
//
// Tier 0 scope (MVP-SCOPE.md): grids are Country x Club, Club x Club (as of
// docs/backlog.md S-030), or, as of S-031 (REQ-108), a Trophy-involving
// pairing (Country x Trophy, Club x Trophy, or Trophy x Trophy) — never
// Country x Country (REQ-107). Which pairing a given instance uses is
// picked once per call (SelectPairing), uniformly at random among whichever
// pairings the seeded reference data can support. Row/column headers are then fixed
// once chosen (REQ-102's "N unique row categories and N unique column
// categories") — rows are picked first (any candidate satisfies REQ-107 on
// its own, since the ban only applies to a Country/Country pairing), then
// columns are picked one at a time, each candidate validated against every
// already-fixed row header before being accepted (REQ-101). A rejected
// candidate is discarded and a new one tried, up to
// GridGenerationOptions.MaxAttempts total attempts (a rarely-hit backstop)
// or GridGenerationOptions.MaxDuration of wall-clock time (ADR-0023 — this
// is what actually bounds a real run, well under any infrastructure
// request timeout) — whichever trips first aborts with GridGenerationException,
// matching REQ-101's abort rule.
public class GridGenerationService(
    IGridInstanceRepository gridInstanceRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IGridLiveLookupDispatcher liveLookupDispatcher,
    GridGenerationOptions options,
    ILogger<GridGenerationService> logger,
    Random? random = null,
    TimeProvider? timeProvider = null) : IGridGenerationService
{
    // SelectPairing's uniform-at-random choice among every feasible pairing
    // goes through this field — candidate-order shuffling still uses
    // Random.Shared, same as before S-030, since no test relies on
    // controlling shuffle order. Optional constructor param (like
    // WikidataClient's queryTimeout) so tests can pin the pairing choice
    // without DI needing to register a Random.
    private readonly Random _random = random ?? Random.Shared;

    // ADR-0023: PickHeadersAsync's own wall-clock deadline reads this
    // rather than DateTime.UtcNow directly, so tests can exercise the
    // deadline-abort branch deterministically. Falls back to the real
    // clock in production the same way RoundGenerationService's
    // TimeProvider does — already registered as TimeProvider.System in
    // Program.cs's DI container, resolved automatically.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");

        // REQ-109: candidate values only ever come from the reference
        // tables, never derived ad hoc from PlayerAttribute.
        var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
        var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
        // ADR-0061: t.IsTeamTrophy threaded through the same way
        // c.UsesCountryForSportProperty is above — see CategoryCandidate's
        // own doc comment.
        var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
            .Select(t => new CategoryCandidate(t.Name, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();

        var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);

        var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
        var colPool = PoolFor(colCategoryType, countries, clubs, trophies);

        // REQ-102: N unique row categories. Any candidate is a valid row
        // header on its own — REQ-107's ban only bites once paired with a
        // column, checked inside PickHeadersAsync below.
        var rowHeaders = Shuffle(rowPool).Take(template.Size).ToList();

        // REQ-102's "no row category may be identical to a column category"
        // only bites when both axes share a category type (Club x Club) —
        // Country and Club values can never collide by name.
        var colCandidatePool = rowCategoryType == colCategoryType
            ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
            : colPool;

        var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);

        var instanceId = Guid.NewGuid();
        var instance = new GridInstance
        {
            Id = instanceId,
            TemplateId = template.Id,
            // GridInstanceId set explicitly rather than left to EF Core's
            // relationship fixup via this navigation — Guid is non-nullable,
            // so an unset value would be Guid.Empty, not an obviously-wrong
            // placeholder EF would know to overwrite.
            Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
        };
        await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);

        return new GameInstance { Id = instance.Id };
    }

    // REQ-107/REQ-108 (S-030, extended S-031): Country x Country is never a
    // candidate, so there's nothing to filter out here, only to choose
    // between. Every other pairing CategoryPairingRules.IsAllowedPairing
    // permits is a candidate: Country x Club, Club x Club, Country x Trophy,
    // Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
    // *second* type in a mixed pairing (Country/Club always first), the
    // same precedent Country x Club already set for Country preceding Club.
    // A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
    // distinct values, since REQ-102 forbids a value appearing on both axes;
    // a mixed pairing just needs >= size in each of the two pools. Chooses
    // uniformly at random among whichever pairings the seeded reference
    // data can actually support — generalizing S-030's two-way coin flip to
    // an N-way choice.
    //
    // Non-obvious consequence, load-bearing for what actually ships (see
    // ReferenceDataSeeder and docs/backlog.md S-031): with only one trophy
    // seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
    // for any realistic grid size, so every Trophy pairing below was
    // infeasible in production — that was expected, not a bug (REQ-108
    // describes the trophy list as reference data meant to grow later, "a
    // data change, not a code change"), and this class's unit tests proved
    // the mechanism itself worked using a larger injected trophy pool, ahead
    // of production data actually triggering it.
    //
    // UPDATE (ADR-0061, 2026-08-09): ReferenceDataSeeder now seeds three
    // trophies (Ballon d'Or, FIFA World Cup, UEFA Champions League), which
    // makes trophyCount(3) >= size for the default GridSize = 3 — Country x
    // Trophy and Club x Trophy are REACHABLE in production now, for the
    // first time, not just a mechanism proven by tests. Trophy x Trophy
    // still needs trophyCount >= size * 2 = 6, so it remains infeasible for
    // now — this will need revisiting if/when the trophy pool grows further.
    private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount, int trophyCount)
    {
        var candidates = new (string RowType, string ColType, bool Feasible)[]
        {
            (CategoryPairingRules.Country, CategoryPairingRules.Club, countryCount >= size && clubCount >= size),
            (CategoryPairingRules.Club, CategoryPairingRules.Club, clubCount >= size * 2),
            (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
            (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
            (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),
        };

        var feasible = candidates.Where(c => c.Feasible).Select(c => (c.RowType, c.ColType)).ToList();

        if (feasible.Count == 0)
        {
            throw new GridGenerationException(
                $"Not enough reference data to build a {size}x{size} grid " +
                $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available).");
        }

        return feasible[_random.Next(feasible.Count)];
    }

    // PlayerAttribute.AttributeType's reference-table equivalent — which
    // seeded pool a given category type's candidates are drawn from.
    // Distinct from CategoryPairingRules.MapAttributeType (that one maps to
    // PlayerAttribute's vocabulary for guess-checking; this one picks a
    // CategoryCandidate pool for generation).
    private static List<CategoryCandidate> PoolFor(
        string categoryType, List<CategoryCandidate> countries, List<CategoryCandidate> clubs, List<CategoryCandidate> trophies) =>
        categoryType switch
        {
            CategoryPairingRules.Country => countries,
            CategoryPairingRules.Club => clubs,
            CategoryPairingRules.Trophy => trophies,
            _ => throw new GridGenerationException($"Unknown category type '{categoryType}'."),
        };

    // REQ-101/107: tries column candidates one at a time (never repeating a
    // rejected one), accepting only those valid against every fixed row
    // header, until N columns are accepted or one of three abort conditions
    // trips: the candidate pool is exhausted, MaxAttempts is hit (a
    // backstop that rarely matters in practice — see its own doc comment),
    // or MaxDuration elapses (ADR-0023 — this is what actually bounds a
    // real run's wall-clock time, well under any infrastructure request
    // timeout, so the caller always gets a definitive answer — success or a
    // clean GridGenerationException — instead of the request being killed
    // out from under it). Generalized by S-030 to work for any pairing of
    // category types, not just Country rows x Club columns.
    //
    // Deliberately still sequential, not concurrent, despite each
    // candidate's live-lookup cost being the dominant source of latency —
    // PlayerStoreRepository/CategoryValueRepository/WikidataLookupService
    // all share one request-scoped XGArcadeDbContext (Program.cs's
    // AddDbContext/AddScoped registrations), and EF Core's DbContext isn't
    // safe for concurrent use by a single instance. Running candidates
    // through Task.WhenAll here would intermittently throw against real
    // Npgsql ("a second operation was started on this context before a
    // previous operation completed") while quietly working against the
    // InMemory provider tests use — exactly the kind of bug that looks
    // fine in CI and breaks in production. Real concurrency would need
    // IDbContextFactory-based per-call contexts threaded through all three
    // components, which is real, valuable follow-up work but a separate,
    // carefully-scoped change, not part of this fix (see ADR-0023).
    private async Task<List<(CategoryCandidate Candidate, int[] MatchCounts)>> PickHeadersAsync(
        string rowCategoryType,
        IReadOnlyList<CategoryCandidate> rowHeaders,
        string colCategoryType,
        IReadOnlyList<CategoryCandidate> colCandidatePool,
        CancellationToken cancellationToken)
    {
        // REQ-107: checked once, before any matching-count query — every
        // column candidate in this call pairs the same two category types
        // (including a Trophy pairing, S-031 — still fixed for the whole
        // call, never varying per candidate), so this is invariant per
        // call. A hypothetical future grid whose row/column category types
        // vary *within* one call would need to check this per candidate
        // instead.
        if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
            throw new GridGenerationException("Country x Country pairing is never allowed (REQ-107).");

        var remaining = Shuffle(colCandidatePool);
        var accepted = new List<(CategoryCandidate, int[])>();
        var attempts = 0;
        var deadline = _timeProvider.GetUtcNow() + options.MaxDuration;

        logger.LogInformation(
            "Picking {Needed} {ColCategoryType} headers against {RowCategoryType} rows from a pool of {PoolSize} candidates (MaxDuration={MaxDuration}).",
            rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);

        while (accepted.Count < rowHeaders.Count)
        {
            EnsurePickingCanContinue(remaining.Count, attempts, accepted.Count, rowHeaders.Count, deadline);

            var candidate = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
            attempts++;

            var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
            if (matchCounts is null)
            {
                logger.LogDebug("Rejected {ColCategoryType} candidate '{Candidate}' — below MinValidAnswers on at least one row.",
                    colCategoryType, candidate.Name);
                continue;
            }

            logger.LogDebug("Accepted {ColCategoryType} candidate '{Candidate}' ({Accepted}/{Needed}).",
                colCategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
            accepted.Add((candidate, matchCounts));
        }

        return accepted;
    }

    // PickHeadersAsync's three abort conditions (pool exhausted, MaxAttempts
    // hit, MaxDuration elapsed), unchanged from the original inline checks —
    // same order, same exception messages, still whichever trips first.
    private void EnsurePickingCanContinue(int remainingCount, int attempts, int acceptedCount, int neededCount, DateTimeOffset deadline)
    {
        if (remainingCount == 0)
            throw new GridGenerationException("Ran out of candidates before completing the grid.");
        if (attempts >= options.MaxAttempts)
            throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");
        if (_timeProvider.GetUtcNow() >= deadline)
            ThrowDeadlineExceeded(remainingCount, attempts, acceptedCount, neededCount);
    }

    private void ThrowDeadlineExceeded(int remainingCount, int attempts, int acceptedCount, int neededCount)
    {
        logger.LogWarning(
            "Grid generation aborted after exceeding MaxDuration ({MaxDuration}): {Accepted}/{Needed} headers " +
            "found in {Attempts} attempts, {Remaining} candidates left untried.",
            options.MaxDuration, acceptedCount, neededCount, attempts, remainingCount);
        throw new GridGenerationException(
            $"Grid generation aborted after exceeding {options.MaxDuration} " +
            $"(found {acceptedCount}/{neededCount} valid headers in {attempts} attempts).");
    }

    // The inner per-candidate validity check PickHeadersAsync's while loop
    // runs against every fixed row header — null means this candidate is
    // rejected (below MinValidAnswers against at least one row), matching
    // the original inline for-loop's early break exactly, just out of the
    // caller's way.
    private async Task<int[]?> TryComputeMatchCountsAsync(
        string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
        string colCategoryType, CategoryCandidate candidate, CancellationToken cancellationToken)
    {
        var matchCounts = new int[rowHeaders.Count];
        for (var i = 0; i < rowHeaders.Count; i++)
        {
            matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
            if (matchCounts[i] < options.MinValidAnswers)
                return null;
        }

        return matchCounts;
    }

    // REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
    // cache miss triggers a live lookup, persisted immediately (never
    // deferred/batched) as WikidataLookupOrigin.Sync — a routine query
    // against Wikidata's own vetted per-category intersection. As of
    // ADR-0032 this origin and REQ-211's narrower guess-time fallback (owned
    // by GridLiveLookupDispatcher) both persist as "verified" (ADR-0029 had
    // trusted only this one as ground truth; ADR-0032 reversed that split),
    // but the two origins are still passed through distinctly for
    // logging/future re-differentiation — see ADR-0032. A category value
    // with no resolved WikidataQid is not an error — the live lookup just
    // returns no matches (REQ-109), which this treats as an ordinary
    // 0-count, handled by the caller's normal retry logic.
    private async Task<int> GetMatchCountAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        CancellationToken cancellationToken)
    {
        var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
            CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
        if (cachedCount > 0)
            return cachedCount;

        var liveMatches = await liveLookupDispatcher.LookupMatchesAsync(
            rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
        return liveMatches?.Count ?? 0;
    }

    private static List<GridCell> BuildCells(
        Guid gridInstanceId,
        string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
        string colCategoryType, IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
    {
        var cells = new List<GridCell>(rowHeaders.Count * columns.Count);
        for (var row = 0; row < rowHeaders.Count; row++)
        {
            for (var col = 0; col < columns.Count; col++)
            {
                cells.Add(CreateCell(
                    gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
            }
        }
        return cells;
    }

    private static GridCell CreateCell(
        Guid gridInstanceId, int row, string rowCategoryType, CategoryCandidate rowHeader,
        int col, string colCategoryType, CategoryCandidate colHeader) =>
        new()
        {
            Id = Guid.NewGuid(),
            GridInstanceId = gridInstanceId,
            Row = row,
            Col = col,
            RowCategoryType = rowCategoryType,
            RowCategoryValue = rowHeader.Name,
            ColCategoryType = colCategoryType,
            ColCategoryValue = colHeader.Name,
        };

    private static List<T> Shuffle<T>(IReadOnlyList<T> source)
    {
        var array = source.ToArray();
        Random.Shared.Shuffle(array);
        return [.. array];
    }
}
