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
// Country x Country (REQ-107).
//
// ADR-0089 (2026-08-29): each row/column header now picks its own category
// type independently, rather than one pairing (e.g. Country x Club) being
// chosen once for the whole instance via the now-removed SelectPairing/
// PoolFor. Row and column headers are both drawn from one combined pool —
// every seeded Country, Club, and Trophy candidate concatenated together,
// shuffled, and taken — so a header's odds of being a given type are
// naturally proportional to how much reference data that type actually has,
// rather than an artificial even split across the 3 types (see the ADR's
// Decision §2/Alternatives for why that specific distribution matters).
// REQ-107's Country x Country ban is checked per (row header, column
// candidate) pair, inside PickHeadersAsync's per-row loop, before that row's
// match-count query — never hoisted back out to a once-per-call check
// against a globally-fixed pairing, since there is no such single pairing
// any more. A rejected column candidate (failed pairing check OR fell below
// MinValidAnswers against some row) is discarded and a new one tried, up to
// GridGenerationOptions.MaxAttempts total attempts (a rarely-hit backstop)
// or GridGenerationOptions.MaxDuration of wall-clock time (ADR-0023 — this
// is what actually bounds a real run, well under any infrastructure request
// timeout) — whichever trips first aborts with GridGenerationException,
// matching REQ-101's abort rule. Row headers are still never retried once
// picked (see ADR-0089's "Negative / trade-offs accepted" — a follow-up if
// this alone doesn't sufficiently reduce "Ran out of candidates" failures).
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
    // ADR-0089: with SelectPairing gone, this field's only remaining job is
    // driving Shuffle below (used for both row-header and column-candidate
    // selection) — previously that shuffle used the hardcoded Random.Shared
    // unconditionally (no test needed to control shuffle order, only
    // SelectPairing's coin flip read this field). Kept as an injectable
    // constructor parameter (defaulting to Random.Shared) for the same
    // reason it always was — so a future caller (e.g. a row-header-retry
    // follow-up, see the ADR's own "Follow-up" note) can supply a seeded
    // Random without DI needing to register one in production.
    // GridGenerationServiceTests deliberately does NOT try to pin this
    // field's output to control which category type a given header ends up
    // as — a custom Random subclass could only reliably do that by matching
    // the exact algorithm .NET's Random.Shuffle uses internally, which this
    // class has no contract with and no local way to verify (no dotnet SDK
    // in this sandbox). Tests instead seed data that is either
    // single-category-type (removing any type-selection ambiguity) or
    // symmetric/order-agnostic (safe regardless of which candidate a real
    // shuffle happens to draw as a row vs. a column) — see this file's own
    // tests for the pattern.
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
        // tables, never derived ad hoc from PlayerAttribute. Each candidate
        // is tagged with its own CategoryType (ADR-0089) up front, so the
        // rest of this method never needs to track "which pool did this
        // come from" separately from the candidate itself.
        var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, CategoryPairingRules.Country, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
        var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, CategoryPairingRules.Club, c.WikidataQid)).ToList();
        // ADR-0061: t.IsTeamTrophy threaded through the same way
        // c.UsesCountryForSportProperty is above — see CategoryCandidate's
        // own doc comment.
        var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
            .Select(t => new CategoryCandidate(t.Name, CategoryPairingRules.Trophy, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();

        // ADR-0089 point 2: ONE combined pool, not a per-type pool selected
        // by a removed SelectPairing — a uniform draw over this concatenated
        // list (via Shuffle+Take below), not a uniform choice among the 3
        // types first, is what makes a header's odds of being a given type
        // proportional to that type's actual reference-data pool size.
        var combinedPool = countries.Concat(clubs).Concat(trophies).ToList();

        EnsureEnoughCandidates("row", combinedPool.Count, template.Size, countries.Count, clubs.Count, trophies.Count);

        // REQ-102: N unique row categories. Any candidate is a valid row
        // header on its own — REQ-107's ban only bites once paired with a
        // column, checked per (row, column-candidate) pair inside
        // PickHeadersAsync below.
        var rowHeaders = Shuffle(combinedPool).Take(template.Size).ToList();

        // REQ-102's "no row category may be identical to a column category"
        // is a per-(CategoryType, Name) equality check (ADR-0089 point 3) —
        // a candidate of a different CategoryType than every row header can
        // never collide by name and is never filtered out here, same
        // assumption the old axis-level check made (a Country and a Club
        // can never collide by name).
        var colCandidatePool = combinedPool
            .Where(c => !rowHeaders.Any(r => r.CategoryType == c.CategoryType && r.Name == c.Name))
            .ToList();

        EnsureEnoughCandidates("column", colCandidatePool.Count, template.Size, countries.Count, clubs.Count, trophies.Count);

        var columns = await PickHeadersAsync(rowHeaders, colCandidatePool, cancellationToken);

        var instanceId = Guid.NewGuid();
        var instance = new GridInstance
        {
            Id = instanceId,
            TemplateId = template.Id,
            // GridInstanceId set explicitly rather than left to EF Core's
            // relationship fixup via this navigation — Guid is non-nullable,
            // so an unset value would be Guid.Empty, not an obviously-wrong
            // placeholder EF would know to overwrite.
            Cells = BuildCells(instanceId, rowHeaders, columns),
        };
        await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);

        return new GameInstance { Id = instance.Id };
    }

    // ADR-0089 point 4: replaces SelectPairing's removed "none of the 5
    // fixed pairing combinations is feasible" upfront throw — a simple
    // combined-pool-size check, applied both to the row pool and (after
    // removing already-used row values) the column candidate pool. A
    // genuinely near-empty reference-data database is the only realistic
    // way to trip this now; GridGenerationOptions.MaxAttempts/MaxDuration
    // (ADR-0023) remain the real backstop for the picking loop itself.
    //
    // `poolLabel`/`poolSize` distinguish which of the two checks tripped —
    // without this, a column-pool failure (e.g. every row header happened
    // to consume the only candidates of a sparse type, see this file's own
    // top-of-file comment and ADR-0089's "Negative / trade-offs accepted")
    // would report the same fixed reference-data totals as a genuine
    // empty-database failure, hiding the actual (possibly zero) remaining
    // pool size an on-call engineer would need to diagnose it quickly.
    private static void EnsureEnoughCandidates(string poolLabel, int poolSize, int size, int countryCount, int clubCount, int trophyCount)
    {
        if (poolSize < size)
        {
            throw new GridGenerationException(
                $"Not enough {poolLabel} candidates ({poolSize} available) to build a {size}x{size} grid " +
                $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available in total).");
        }
    }

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
    // category types, and by ADR-0089 to work when each row/column header
    // carries its own independently-chosen category type.
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
        IReadOnlyList<CategoryCandidate> rowHeaders,
        IReadOnlyList<CategoryCandidate> colCandidatePool,
        CancellationToken cancellationToken)
    {
        var remaining = Shuffle(colCandidatePool);
        var accepted = new List<(CategoryCandidate, int[])>();
        var attempts = 0;
        var deadline = _timeProvider.GetUtcNow() + options.MaxDuration;

        logger.LogInformation(
            "Picking {Needed} column headers from a combined pool of {PoolSize} candidates (MaxDuration={MaxDuration}).",
            rowHeaders.Count, remaining.Count, options.MaxDuration);

        while (accepted.Count < rowHeaders.Count)
        {
            EnsurePickingCanContinue(remaining.Count, attempts, accepted.Count, rowHeaders.Count, deadline);

            var candidate = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
            attempts++;

            var matchCounts = await TryComputeMatchCountsAsync(rowHeaders, candidate, cancellationToken);
            if (matchCounts is null)
            {
                logger.LogDebug(
                    "Rejected {CategoryType} candidate '{Candidate}' — either an unallowed pairing (REQ-107) or below MinValidAnswers on at least one row.",
                    candidate.CategoryType, candidate.Name);
                continue;
            }

            logger.LogDebug("Accepted {CategoryType} candidate '{Candidate}' ({Accepted}/{Needed}).",
                candidate.CategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
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
    // rejected, matching the original inline for-loop's early break exactly,
    // just out of the caller's way.
    //
    // ADR-0089: REQ-107's Country x Country ban is checked here, per (row,
    // candidate) pair, BEFORE that row's match-count query — the position
    // REQ-107 requires — rather than once outside this loop against a
    // globally-fixed pairing (removed along with SelectPairing, since there
    // is no longer one pairing shared by every row). A failed pairing check
    // rejects this candidate for the whole call, the same way a
    // MinValidAnswers failure does: return null, candidate discarded, the
    // caller tries the next one.
    private async Task<int[]?> TryComputeMatchCountsAsync(
        IReadOnlyList<CategoryCandidate> rowHeaders, CategoryCandidate candidate, CancellationToken cancellationToken)
    {
        var matchCounts = new int[rowHeaders.Count];
        for (var i = 0; i < rowHeaders.Count; i++)
        {
            if (!CategoryPairingRules.IsAllowedPairing(rowHeaders[i].CategoryType, candidate.CategoryType))
                return null;

            matchCounts[i] = await GetMatchCountAsync(rowHeaders[i], candidate, cancellationToken);
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
    private async Task<int> GetMatchCountAsync(CategoryCandidate row, CategoryCandidate col, CancellationToken cancellationToken)
    {
        var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            CategoryPairingRules.MapAttributeType(row.CategoryType), row.Name,
            CategoryPairingRules.MapAttributeType(col.CategoryType), col.Name, cancellationToken);
        if (cachedCount > 0)
            return cachedCount;

        var liveMatches = await liveLookupDispatcher.LookupMatchesAsync(
            row.CategoryType, row, col.CategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
        return liveMatches?.Count ?? 0;
    }

    private static List<GridCell> BuildCells(
        Guid gridInstanceId,
        IReadOnlyList<CategoryCandidate> rowHeaders,
        IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
    {
        var cells = new List<GridCell>(rowHeaders.Count * columns.Count);
        for (var row = 0; row < rowHeaders.Count; row++)
        {
            for (var col = 0; col < columns.Count; col++)
            {
                cells.Add(CreateCell(gridInstanceId, row, rowHeaders[row], col, columns[col].Candidate));
            }
        }
        return cells;
    }

    // ADR-0089: RowCategoryType/ColCategoryType now come from each header's
    // own CategoryType, not one constant passed in per axis — GridCell
    // already stores these per cell, not per instance, so no schema change
    // was needed for this.
    private static GridCell CreateCell(
        Guid gridInstanceId, int row, CategoryCandidate rowHeader, int col, CategoryCandidate colHeader) =>
        new()
        {
            Id = Guid.NewGuid(),
            GridInstanceId = gridInstanceId,
            Row = row,
            Col = col,
            RowCategoryType = rowHeader.CategoryType,
            RowCategoryValue = rowHeader.Name,
            ColCategoryType = colHeader.CategoryType,
            ColCategoryValue = colHeader.Name,
        };

    private List<T> Shuffle<T>(IReadOnlyList<T> source)
    {
        var array = source.ToArray();
        _random.Shuffle(array);
        return [.. array];
    }
}
