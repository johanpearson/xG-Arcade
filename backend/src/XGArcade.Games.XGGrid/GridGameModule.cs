using Microsoft.Extensions.Logging;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// COMP-05: IGameModule implementation for the xG Grid game.
//
// Tier 0 scope (MVP-SCOPE.md): grids are always Country x Club or, as of
// docs/backlog.md S-030, Club x Club — never Country x Country (REQ-107),
// never Trophy (REQ-108, deferred). Which pairing a given instance uses is
// picked once per call (SelectPairing), randomly whenever the seeded
// reference data can support either. Row/column headers are then fixed
// once chosen (REQ-102's "N unique row categories and N unique column
// categories") — rows are picked first (any candidate satisfies REQ-107 on
// its own, since the ban only applies to a Country/Country pairing), then
// columns are picked one at a time, each candidate validated against every
// already-fixed row header before being accepted (REQ-101). A rejected
// candidate is discarded and a new one tried, up to
// GridGenerationOptions.MaxAttempts total attempts across the whole
// instance, matching REQ-101's abort rule.
public class GridGameModule(
    IGridInstanceRepository gridInstanceRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerStoreRepository playerStoreRepository,
    IWikidataLookupService wikidataLookupService,
    GridGenerationOptions options,
    ILogger<GridGameModule> logger,
    Random? random = null) : IGameModule
{
    public const string XGGridGameKey = "xg-grid";

    // PlayerAttribute.AttributeType's vocabulary for these two category
    // types — see CategoryPairingRules' doc comment for why this differs
    // from "country"/"club".
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

    // Only the Country x Club / Club x Club pairing coin-flip goes through
    // this field (see SelectPairing) — candidate-order shuffling still uses
    // Random.Shared, same as before S-030, since no test relies on
    // controlling shuffle order. Optional constructor param (like
    // WikidataClient's queryTimeout) so tests can pin the pairing choice
    // without DI needing to register a Random.
    private readonly Random _random = random ?? Random.Shared;

    public string GameKey => XGGridGameKey;

    // A row/column header candidate, abstracted away from which reference
    // table (CountryDefinition/ClubDefinition) it came from — REQ-107's
    // generalized pairing selection (S-030) needs to treat both uniformly.
    private readonly record struct CategoryCandidate(string Name, string? WikidataQid);

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");

        // REQ-109: candidate values only ever come from the reference
        // tables, never derived ad hoc from PlayerAttribute.
        var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
        var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
            .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();

        var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count);

        var rowPool = rowCategoryType == CategoryPairingRules.Country ? countries : clubs;
        var colPool = colCategoryType == CategoryPairingRules.Country ? countries : clubs;

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

    // REQ-107 (S-030): Country x Club and Club x Club are the only two
    // pairings Tier 0 ever generates (Trophy is Tier 1, REQ-108) —
    // Country x Country is never a candidate, so there's nothing to filter
    // out here, only to choose between. Prefers whichever pairing(s) the
    // seeded reference data can actually support (Club x Club needs 2xSize
    // distinct clubs, since REQ-102 forbids a value appearing on both
    // axes), choosing randomly between the two only when both are feasible.
    private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount)
    {
        var countryClubFeasible = countryCount >= size && clubCount >= size;
        var clubClubFeasible = clubCount >= size * 2;

        if (!countryClubFeasible && !clubClubFeasible)
        {
            throw new GridGenerationException(
                $"Not enough reference data to build a {size}x{size} grid " +
                $"({countryCount} countries, {clubCount} clubs available).");
        }

        if (countryClubFeasible && clubClubFeasible)
        {
            return _random.Next(2) == 0
                ? (CategoryPairingRules.Country, CategoryPairingRules.Club)
                : (CategoryPairingRules.Club, CategoryPairingRules.Club);
        }

        return countryClubFeasible
            ? (CategoryPairingRules.Country, CategoryPairingRules.Club)
            : (CategoryPairingRules.Club, CategoryPairingRules.Club);
    }

    // S-009: REQ-210's lock/attempt-cap checks and REQ-202's guess-change
    // policy already happened in Core.Scoring before this was ever called
    // (GuessSubmissionService) — everything here is REQ-207/208/209/211's
    // name-resolution work.
    public async Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        var guessSubmission = (GuessSubmission)submission;

        var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");

        var cell = instance.Cells.FirstOrDefault(c => c.Id == guessSubmission.CellId)
            ?? throw new GuessScoringException($"Cell '{guessSubmission.CellId}' not found in grid instance '{instanceId}'.");

        // REQ-208 (Tier 0's simple half, MVP-SCOPE.md): normalize only — no
        // PlayerAlias/fuzzy tolerance (both deferred, "defer the alias table
        // and fuzzy typo tolerance").
        var normalized = PlayerNameNormalizer.Normalize(guessSubmission.SubmittedName);

        var result = await FindMatchAsync(cell, normalized, instanceId, cancellationToken);
        if (result.IsCorrect)
            return result;

        // REQ-211 (Tier 0 simplified — no PlayerNameIndex prerequisite,
        // ADR-0010's documented gap): grid generation's cached match count
        // (REQ-101/MinValidAnswers) only ever needed to prove this cell had
        // *some* valid answers, never to catalog every one, so a guess can
        // be genuinely correct even though nothing cached confirms it yet —
        // either because this exact player was never synced at all, or
        // because they already exist with one category's attribute cached
        // (from an unrelated cell) but not this cell's other one. Re-running
        // this cell's own country x club intersection query is an upsert,
        // not a fresh insert (WikidataLookupService.GetOrCreatePlayerAsync),
        // so one call fixes both cases and completes the cell's whole
        // answer key for later guesses too, not just this one name. Full
        // REQ-211 gates this on a PlayerNameIndex match first (Tier 1, not
        // built) to keep the trigger narrow against a scarce API budget;
        // Wikidata alone isn't meaningfully capped (ADR-0011), so Tier 0
        // skips that prerequisite and simply retries once per guess that
        // didn't already resolve from cache — bounded by REQ-210's 2-attempt
        // cap, same as every other guess-time cost.
        if (!await RefreshCellFromLiveLookupAsync(cell, cancellationToken))
            return result;

        return await FindMatchAsync(cell, normalized, instanceId, cancellationToken);
    }

    // ADR-0021: round-close's unanswered-cell penalty needs every cell id
    // for the instance, regardless of whether anyone ever guessed it.
    public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
            ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");

        return instance.Cells.Select(c => c.Id).ToList();
    }

    private async Task<ScoreResult> FindMatchAsync(
        GridCell cell, string normalizedName, Guid instanceId, CancellationToken cancellationToken)
    {
        var candidates = await playerStoreRepository.GetPlayersByNormalizedFullNameAsync(normalizedName, cancellationToken);

        var matching = new List<Player>();
        foreach (var candidate in candidates)
        {
            var satisfiesRow = await playerStoreRepository.HasEffectiveAttributeAsync(
                candidate.Id, MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue, cancellationToken);
            if (!satisfiesRow)
                continue;

            var satisfiesCol = await playerStoreRepository.HasEffectiveAttributeAsync(
                candidate.Id, MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue, cancellationToken);
            if (satisfiesCol)
                matching.Add(candidate);
        }

        if (matching.Count == 0)
            return new ScoreResult { IsCorrect = false };

        // REQ-204: identical guesses by different players must always group
        // as the same answer — the lowest Id among fits is the deterministic
        // pick, same rule REQ-209's simplified Tier 0 disambiguation uses.
        var accepted = matching.OrderBy(p => p.Id).First();

        if (matching.Count > 1)
        {
            // REQ-209 (Tier 0 simplified, MVP-SCOPE.md): any fitting
            // candidate is accepted, no disambiguation picker — logged so a
            // real occurrence trips the Tier 1 "disambiguation UI" trigger
            // ("log this case even in the simplified Tier 0 handling, so
            // you'd notice if it happened").
            logger.LogWarning(
                "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +
                "accepted the lowest Id ({PlayerId}) per REQ-204's deterministic-pick rule.",
                cell.Id, instanceId, matching.Count, accepted.Id);
        }

        return new ScoreResult { IsCorrect = true, PlayerAnswerId = accepted.Id };
    }

    // REQ-211's Tier 0 fallback (ADR-0018) knows how to refresh a
    // Country x Club cell and, as of S-030, a Club x Club cell too — any
    // other pairing (e.g. a future Trophy cell) can't be resolved from the
    // reference tables this way at all, and is left to fail closed via the
    // caller's existing cached-only result, same as a genuinely-incorrect
    // guess.
    private async Task<bool> RefreshCellFromLiveLookupAsync(GridCell cell, CancellationToken cancellationToken)
    {
        if (cell.RowCategoryType == CategoryPairingRules.Country && cell.ColCategoryType == CategoryPairingRules.Club)
        {
            var country = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
                .FirstOrDefault(c => c.Name == cell.RowCategoryValue);
            var club = (await categoryValueRepository.GetClubsAsync(cancellationToken))
                .FirstOrDefault(c => c.Name == cell.ColCategoryValue);
            if (country is null || club is null)
                return false;

            await wikidataLookupService.LookupAndPersistAsync(country, club, cancellationToken);
            return true;
        }

        if (cell.RowCategoryType == CategoryPairingRules.Club && cell.ColCategoryType == CategoryPairingRules.Club)
        {
            var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
            var rowClub = clubs.FirstOrDefault(c => c.Name == cell.RowCategoryValue);
            var colClub = clubs.FirstOrDefault(c => c.Name == cell.ColCategoryValue);
            if (rowClub is null || colClub is null)
                return false;

            await wikidataLookupService.LookupAndPersistClubClubAsync(rowClub, colClub, cancellationToken);
            return true;
        }

        return false;
    }

    // PlayerAttribute.AttributeType's vocabulary ("nationality" | "club")
    // differs from GridCell's RowCategoryType/ColCategoryType vocabulary
    // ("country" | "club") — same mapping GetMatchCountAsync below already
    // needs for grid generation.
    private static string MapAttributeType(string categoryType) => categoryType switch
    {
        CategoryPairingRules.Country => NationalityAttributeType,
        CategoryPairingRules.Club => ClubAttributeType,
        _ => throw new GuessScoringException($"Unknown category type '{categoryType}'."),
    };

    // REQ-101/107: tries column candidates one at a time (never repeating a
    // rejected one), accepting only those valid against every fixed row
    // header, until N columns are accepted or MaxAttempts is exhausted.
    // Generalized by S-030 to work for any pairing of category types, not
    // just Country rows x Club columns.
    private async Task<List<(CategoryCandidate Candidate, int[] MatchCounts)>> PickHeadersAsync(
        string rowCategoryType,
        IReadOnlyList<CategoryCandidate> rowHeaders,
        string colCategoryType,
        IReadOnlyList<CategoryCandidate> colCandidatePool,
        CancellationToken cancellationToken)
    {
        // REQ-107: checked once, before any matching-count query — every
        // column candidate in this call pairs the same two category types,
        // so this is invariant per call, not per candidate. Tier 1 mixed
        // axes (e.g. Trophy) would call this per candidate instead, once
        // row/column category types can vary within one grid.
        if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
            throw new GridGenerationException("Country x Country pairing is never allowed (REQ-107).");

        var remaining = Shuffle(colCandidatePool);
        var accepted = new List<(CategoryCandidate, int[])>();
        var attempts = 0;

        while (accepted.Count < rowHeaders.Count)
        {
            if (remaining.Count == 0)
                throw new GridGenerationException("Ran out of candidates before completing the grid.");
            if (attempts >= options.MaxAttempts)
                throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");

            var candidate = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
            attempts++;

            var matchCounts = new int[rowHeaders.Count];
            var isValid = true;
            for (var i = 0; i < rowHeaders.Count; i++)
            {
                matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
                if (matchCounts[i] < options.MinValidAnswers)
                {
                    isValid = false;
                    break;
                }
            }

            if (isValid)
                accepted.Add((candidate, matchCounts));
        }

        return accepted;
    }

    // REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
    // cache miss triggers a live lookup, persisted immediately as unverified
    // data (never deferred/batched). A category value with no resolved
    // WikidataQid is not an error — the live lookup just returns no matches
    // (REQ-109), which this treats as an ordinary 0-count, handled by the
    // caller's normal retry logic.
    private async Task<int> GetMatchCountAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        CancellationToken cancellationToken)
    {
        var cachedCount = await playerStoreRepository.CountPlayersWithBothAttributesAsync(
            MapAttributeType(rowCategoryType), row.Name, MapAttributeType(colCategoryType), col.Name, cancellationToken);
        if (cachedCount > 0)
            return cachedCount;

        var liveMatches = await LookupLiveMatchesAsync(rowCategoryType, row, colCategoryType, col, cancellationToken);
        return liveMatches.Count;
    }

    // Dispatches to whichever IWikidataLookupService method matches this
    // pairing. WikidataLookupService only ever reads Name/WikidataQid off
    // the CountryDefinition/ClubDefinition it's given (never Id) — safe to
    // construct throwaway instances here rather than threading the real
    // reference-table rows through the whole candidate-picking pipeline
    // just for an Id nothing downstream uses.
    private async Task<IReadOnlyList<Player>> LookupLiveMatchesAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        CancellationToken cancellationToken)
    {
        if (rowCategoryType == CategoryPairingRules.Country && colCategoryType == CategoryPairingRules.Club)
        {
            return await wikidataLookupService.LookupAndPersistAsync(
                new CountryDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                new ClubDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                cancellationToken);
        }

        if (rowCategoryType == CategoryPairingRules.Club && colCategoryType == CategoryPairingRules.Club)
        {
            return await wikidataLookupService.LookupAndPersistClubClubAsync(
                new ClubDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                new ClubDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                cancellationToken);
        }

        return [];
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
                cells.Add(new GridCell
                {
                    Id = Guid.NewGuid(),
                    GridInstanceId = gridInstanceId,
                    Row = row,
                    Col = col,
                    RowCategoryType = rowCategoryType,
                    RowCategoryValue = rowHeaders[row].Name,
                    ColCategoryType = colCategoryType,
                    ColCategoryValue = columns[col].Candidate.Name,
                });
            }
        }
        return cells;
    }

    private static List<T> Shuffle<T>(IReadOnlyList<T> source)
    {
        var array = source.ToArray();
        Random.Shared.Shuffle(array);
        return [.. array];
    }
}
