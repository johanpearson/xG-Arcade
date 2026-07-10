using Microsoft.Extensions.Logging;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// COMP-05: IGameModule implementation for the xG Grid game.
//
// Tier 0 scope (MVP-SCOPE.md): grids are always Country (row headers) x
// Club (column headers) — never Country x Country (REQ-107), never Trophy
// (REQ-108, deferred). Row/column headers are fixed once chosen (REQ-102's
// "N unique row categories and N unique column categories") — rows are
// picked first (any country satisfies REQ-107 on its own, since the ban
// only applies to a Country/Country pairing), then columns are picked one
// at a time, each candidate validated against every already-fixed row
// header before being accepted (REQ-101). A rejected candidate is
// discarded and a new one tried, up to GridGenerationOptions.MaxAttempts
// total attempts across the whole instance, matching REQ-101's abort rule.
public class GridGameModule(
    IGridInstanceRepository gridInstanceRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerStoreRepository playerStoreRepository,
    IWikidataLookupService wikidataLookupService,
    GridGenerationOptions options,
    ILogger<GridGameModule> logger) : IGameModule
{
    public const string XGGridGameKey = "xg-grid";

    // PlayerAttribute.AttributeType's vocabulary for these two category
    // types — see CategoryPairingRules' doc comment for why this differs
    // from "country"/"club".
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

    public string GameKey => XGGridGameKey;

    public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
            ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");

        // REQ-109: candidate values only ever come from the reference
        // tables, never derived ad hoc from PlayerAttribute.
        var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);

        if (countries.Count < template.Size || clubs.Count < template.Size)
        {
            throw new GridGenerationException(
                $"Not enough reference data to build a {template.Size}x{template.Size} grid " +
                $"({countries.Count} countries, {clubs.Count} clubs available).");
        }

        // REQ-102: N unique row categories. Any country is a valid row
        // header on its own — REQ-107's ban only bites once paired with a
        // column, checked below.
        var rowHeaders = Shuffle(countries).Take(template.Size).ToList();
        var columns = await PickColumnHeadersAsync(rowHeaders, clubs, cancellationToken);

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
        var candidates = await playerStoreRepository.GetPlayersByNormalizedFullNameAsync(normalized, cancellationToken);

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

    // REQ-101/107: tries club candidates one at a time (never repeating a
    // rejected one), accepting only those valid against every fixed row
    // header, until N columns are accepted or MaxAttempts is exhausted.
    private async Task<List<(ClubDefinition Club, int[] MatchCounts)>> PickColumnHeadersAsync(
        IReadOnlyList<CountryDefinition> rowHeaders,
        IReadOnlyList<ClubDefinition> clubCandidatePool,
        CancellationToken cancellationToken)
    {
        // REQ-107: checked once, before any matching-count query — every
        // column candidate in this call pairs the same two category types
        // (Country rows x Club columns), so this is invariant per call, not
        // per candidate. Tier 1 mixed axes would call this per candidate
        // instead, once row/column category types can vary within one grid.
        if (!CategoryPairingRules.IsAllowedPairing(CategoryPairingRules.Country, CategoryPairingRules.Club))
            throw new GridGenerationException("Country x Country pairing is never allowed (REQ-107).");

        var remainingClubs = Shuffle(clubCandidatePool);
        var accepted = new List<(ClubDefinition, int[])>();
        var attempts = 0;

        while (accepted.Count < rowHeaders.Count)
        {
            if (remainingClubs.Count == 0)
                throw new GridGenerationException("Ran out of club candidates before completing the grid.");
            if (attempts >= options.MaxAttempts)
                throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");

            var candidate = remainingClubs[^1];
            remainingClubs.RemoveAt(remainingClubs.Count - 1);
            attempts++;

            var matchCounts = new int[rowHeaders.Count];
            var isValid = true;
            for (var i = 0; i < rowHeaders.Count; i++)
            {
                matchCounts[i] = await GetMatchCountAsync(rowHeaders[i], candidate, cancellationToken);
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
    // WikidataQid is not an error — LookupAndPersistAsync just returns no
    // matches (REQ-109), which this treats as an ordinary 0-count, handled
    // by the caller's normal retry logic.
    private async Task<int> GetMatchCountAsync(CountryDefinition country, ClubDefinition club, CancellationToken cancellationToken)
    {
        var cachedCount = await playerStoreRepository.CountPlayersWithBothAttributesAsync(
            NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
        if (cachedCount > 0)
            return cachedCount;

        var liveMatches = await wikidataLookupService.LookupAndPersistAsync(country, club, cancellationToken);
        return liveMatches.Count;
    }

    private static List<GridCell> BuildCells(
        Guid gridInstanceId,
        IReadOnlyList<CountryDefinition> rowHeaders,
        IReadOnlyList<(ClubDefinition Club, int[] MatchCounts)> columns)
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
                    RowCategoryType = CategoryPairingRules.Country,
                    RowCategoryValue = rowHeaders[row].Name,
                    ColCategoryType = CategoryPairingRules.Club,
                    ColCategoryValue = columns[col].Club.Name,
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
