using Microsoft.Extensions.Logging;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// COMP-05: IGameModule implementation for the xG Grid game.
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
public class GridGameModule(
    IGridInstanceRepository gridInstanceRepository,
    ICategoryValueRepository categoryValueRepository,
    IPlayerStoreRepository playerStoreRepository,
    IWikidataLookupService wikidataLookupService,
    GridGenerationOptions options,
    ILogger<GridGameModule> logger,
    Random? random = null,
    TimeProvider? timeProvider = null) : IGameModule
{
    public const string XGGridGameKey = "xg-grid";

    // PlayerAttribute.AttributeType's vocabulary for these category types —
    // see CategoryPairingRules' doc comment for why this differs from
    // "country"/"club"/"trophy". Trophy's own AttributeType happens to be
    // spelled identically ("trophy") in both vocabularies, so it needs no
    // constant of its own here (see MapAttributeType).
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

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

    public string GameKey => XGGridGameKey;

    // A row/column header candidate, abstracted away from which reference
    // table (CountryDefinition/ClubDefinition/TrophyDefinition) it came from
    // — REQ-107's generalized pairing selection (S-030, extended S-031)
    // needs to treat all three uniformly.
    //
    // REQ-114/ADR-0035: `UsesCountryForSportProperty` carries
    // CountryDefinition's per-row query-property flag through generation
    // and the guess-time fallback to the point LookupLiveMatchesAsync
    // actually dispatches a live Wikidata call — the smaller, cleaner diff
    // versus re-resolving the full CountryDefinition row by name at
    // dispatch time (which PickHeadersAsync's hot loop would otherwise do
    // once per GetMatchCountAsync call, a real extra query cost that
    // ResolveCandidateAsync's single per-guess lookup doesn't have to
    // justify). Meaningless for Club/Trophy candidates — always false
    // there, never read for those types.
    private readonly record struct CategoryCandidate(string Name, string? WikidataQid, bool UsesCountryForSportProperty = false);

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
        var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
            .Select(t => new CategoryCandidate(t.Name, t.WikidataQid)).ToList();

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
    // seeded (Ballon d'Or), trophyCount(1) is smaller than `size` for any
    // realistic grid size, so every Trophy pairing below is infeasible in
    // production — Trophy can never actually be selected yet. That's
    // expected, not a bug: REQ-108 describes the trophy list as reference
    // data meant to grow later ("a data change, not a code change"), so this
    // mechanism only becomes live once more trophies are added — see this
    // class's unit tests for proof the mechanism itself works, using a
    // larger injected trophy pool.
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
    // Distinct from MapAttributeType below (that one maps to
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

        // REQ-208: normalize once — FindMatchAsync below applies the
        // normalized/alias/fuzzy comparisons in order (exact primary name,
        // then alias, then bounded fuzzy).
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

    // REQ-208's three-stage matching order — exact primary name, then
    // alias, then bounded fuzzy — each stage only runs if the previous one
    // resolved to zero candidates satisfying both of the cell's categories.
    // Each stage reuses FilterByCategoriesAsync/AcceptMatch so REQ-209's
    // Tier 0 disambiguation rule (any fitting candidate accepted,
    // deterministically the lowest Id, logged if more than one fits) applies
    // identically regardless of which stage produced the candidates.
    private async Task<ScoreResult> FindMatchAsync(
        GridCell cell, string normalizedName, Guid instanceId, CancellationToken cancellationToken)
    {
        var exactCandidates = await playerStoreRepository.GetPlayersByNormalizedFullNameAsync(normalizedName, cancellationToken);
        var matching = await FilterByCategoriesAsync(cell, exactCandidates, cancellationToken);

        if (matching.Count == 0)
        {
            // REQ-208: known aliases/stage names, matched via PlayerAlias —
            // an exact NormalizedAlias equality check, same normalization as
            // the primary-name path (PlayerNameNormalizer.Normalize applied
            // at persist time, WikidataLookupService.PersistAliasesAsync).
            var aliasCandidates = await playerStoreRepository.GetPlayersByNormalizedAliasAsync(normalizedName, cancellationToken);
            matching = await FilterByCategoriesAsync(cell, aliasCandidates, cancellationToken);
        }

        if (matching.Count == 0)
        {
            // REQ-208: minor-typo tolerance — only reached when neither an
            // exact primary-name nor an exact alias match resolved anything,
            // per REQ-208's own ordering ("applied only when no exact or
            // alias match is found").
            var fuzzyCandidates = await FindFuzzyCandidatesAsync(cell, normalizedName, cancellationToken);
            matching = await FilterByCategoriesAsync(cell, fuzzyCandidates, cancellationToken);
        }

        return AcceptMatch(cell, instanceId, matching);
    }

    // The category-fit half of FindMatchAsync's pipeline, shared by every
    // stage: a candidate is only ever a real answer for this cell if it
    // satisfies both the row and column category (REQ-203's effective-data
    // check, override-aware).
    private async Task<List<Player>> FilterByCategoriesAsync(
        GridCell cell, IReadOnlyList<Player> candidates, CancellationToken cancellationToken)
    {
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

        return matching;
    }

    // REQ-209 (Tier 0 simplified, MVP-SCOPE.md): any fitting candidate is
    // accepted, no disambiguation picker — REQ-204's deterministic pick
    // (lowest Id) is used whenever more than one candidate fits, logged so a
    // real occurrence trips the Tier 1 "disambiguation UI" trigger. Shared
    // by every stage of FindMatchAsync above so this rule can't drift
    // between the exact/alias/fuzzy paths.
    private ScoreResult AcceptMatch(GridCell cell, Guid instanceId, IReadOnlyList<Player> matching)
    {
        if (matching.Count == 0)
            return new ScoreResult { IsCorrect = false };

        var accepted = matching.OrderBy(p => p.Id).First();

        if (matching.Count > 1)
        {
            logger.LogWarning(
                "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +
                "accepted the lowest Id ({PlayerId}) per REQ-204's deterministic-pick rule.",
                cell.Id, instanceId, matching.Count, accepted.Id);
        }

        return new ScoreResult { IsCorrect = true, PlayerAnswerId = accepted.Id };
    }

    // REQ-208's fuzzy/edit-distance pass. Bounded candidate pool: only
    // players already known (via a cached PlayerAttribute row) to satisfy at
    // least one of this cell's two categories — a player satisfying neither
    // can never be a correct answer for this cell regardless of name, so
    // narrowing here loses no genuine match while keeping the per-guess cost
    // bounded by this cell's own category population, never a full-table
    // scan across every player in the store. Both the candidate's primary
    // name and every recorded alias are checked — a typo of an alias
    // deserves the same tolerance as a typo of the primary name.
    private async Task<IReadOnlyList<Player>> FindFuzzyCandidatesAsync(
        GridCell cell, string normalizedName, CancellationToken cancellationToken)
    {
        var pool = await playerStoreRepository.GetPlayersWithEitherAttributeAsync(
            MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue,
            MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue,
            cancellationToken);

        if (pool.Count == 0)
            return [];

        var aliasesByPlayerId = await playerStoreRepository.GetPlayerAliasesByPlayerIdsAsync(
            pool.Select(p => p.Id).ToList(), cancellationToken);

        var maxDistance = MaxEditDistance(normalizedName.Length);
        var fuzzyMatches = new List<Player>();

        foreach (var candidate in pool)
        {
            if (NameEditDistance.Distance(normalizedName, candidate.NormalizedFullName) <= maxDistance)
            {
                fuzzyMatches.Add(candidate);
                continue;
            }

            if (aliasesByPlayerId.TryGetValue(candidate.Id, out var aliases) &&
                aliases.Any(alias => NameEditDistance.Distance(normalizedName, alias.NormalizedAlias) <= maxDistance))
            {
                fuzzyMatches.Add(candidate);
            }
        }

        return fuzzyMatches;
    }

    // REQ-208: "a small edit-distance tolerance" — three tiers, proportional
    // to the guessed name's normalized length rather than one fixed number
    // for every name. Measured against real name pairs (NameEditDistance),
    // not guessed:
    //   - length <= 4 (e.g. "pele", "zico", "kaka"): tolerance 0 (exact
    //     only). Real 4-letter football nicknames collide at distance 1 far
    //     too often to safely tolerate — "pele" vs "dele" (Dele Alli's own
    //     nickname) is distance 1, and those are two different real
    //     players. At this length, any fuzzy pass would already have been
    //     an exact/alias hit if it were the "same" name, so 0 here costs
    //     nothing genuine while closing that collision.
    //   - length 5-8 (e.g. "zidane", "ronaldo"): tolerance 1. Covers a
    //     single dropped/doubled/substituted letter ("zidane" -> "zidan" is
    //     distance 1) while still rejecting two different real players of
    //     similar length — "ronaldo" vs "rivaldo" is distance 2, correctly
    //     over this tier's tolerance of 1.
    //   - length >= 9 (e.g. "ronaldinho", full "first last" names):
    //     tolerance 2. A two-character slip is still a small fraction of a
    //     name this long ("ronaldinho" -> "ronaldinoh", a trailing
    //     transposition, is distance 2) and stays well short of matching an
    //     unrelated name of similar length (a genuinely different full name
    //     is reliably >2 edits away).
    private static int MaxEditDistance(int normalizedNameLength) => normalizedNameLength switch
    {
        <= 4 => 0,
        <= 8 => 1,
        _ => 2,
    };

    // REQ-211's Tier 0 fallback (ADR-0018) knows how to refresh a
    // Country x Club cell, a Club x Club cell (S-030), and, as of S-031, a
    // Country x Trophy or Club x Trophy cell — any other pairing (e.g.
    // Trophy x Trophy, which has no dedicated persist method — see
    // LookupLiveMatchesAsync's own comment) can't be resolved from the
    // reference tables this way at all, and is left to fail closed via the
    // caller's existing cached-only result, same as a genuinely-incorrect
    // guess. Routes through the same LookupLiveMatchesAsync dispatcher
    // GetMatchCountAsync uses during generation, rather than a second,
    // independently-written pairing check — LookupLiveMatchesAsync returns
    // null for a pairing it doesn't handle, which is exactly this method's
    // fail-closed signal.
    private async Task<bool> RefreshCellFromLiveLookupAsync(GridCell cell, CancellationToken cancellationToken)
    {
        var row = await ResolveCandidateAsync(cell.RowCategoryType, cell.RowCategoryValue, cancellationToken);
        var col = await ResolveCandidateAsync(cell.ColCategoryType, cell.ColCategoryValue, cancellationToken);
        if (row is null || col is null)
            return false;

        var liveMatches = await LookupLiveMatchesAsync(
            cell.RowCategoryType, row.Value, cell.ColCategoryType, col.Value,
            WikidataLookupOrigin.GuessTimeFallback, cancellationToken);
        return liveMatches is not null;
    }

    // Looks a single category value up in whichever reference table its type
    // points at — null if the type is unrecognized or the value isn't a row
    // in that table (REQ-109: shouldn't happen in practice, since generation
    // only ever picks from these tables, but guess-checking must still fail
    // closed rather than throw for a malformed/legacy cell).
    private async Task<CategoryCandidate?> ResolveCandidateAsync(
        string categoryType, string categoryValue, CancellationToken cancellationToken)
    {
        if (categoryType == CategoryPairingRules.Country)
        {
            var country = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
                .FirstOrDefault(c => c.Name == categoryValue);
            return country is null ? null : new CategoryCandidate(country.Name, country.WikidataQid, country.UsesCountryForSportProperty);
        }

        if (categoryType == CategoryPairingRules.Club)
        {
            var club = (await categoryValueRepository.GetClubsAsync(cancellationToken))
                .FirstOrDefault(c => c.Name == categoryValue);
            return club is null ? null : new CategoryCandidate(club.Name, club.WikidataQid);
        }

        if (categoryType == CategoryPairingRules.Trophy)
        {
            var trophy = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
                .FirstOrDefault(t => t.Name == categoryValue);
            return trophy is null ? null : new CategoryCandidate(trophy.Name, trophy.WikidataQid);
        }

        return null;
    }

    // PlayerAttribute.AttributeType's vocabulary ("nationality" | "club" |
    // "trophy") differs from GridCell's RowCategoryType/ColCategoryType
    // vocabulary ("country" | "club" | "trophy") only for Country — Trophy
    // happens to be spelled identically in both, per REQ-108's acceptance
    // text. Same mapping GetMatchCountAsync below already needs for grid
    // generation.
    private static string MapAttributeType(string categoryType) => categoryType switch
    {
        CategoryPairingRules.Country => NationalityAttributeType,
        CategoryPairingRules.Club => ClubAttributeType,
        CategoryPairingRules.Trophy => CategoryPairingRules.Trophy,
        _ => throw new GuessScoringException($"Unknown category type '{categoryType}'."),
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
            if (remaining.Count == 0)
                throw new GridGenerationException("Ran out of candidates before completing the grid.");
            if (attempts >= options.MaxAttempts)
                throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                logger.LogWarning(
                    "Grid generation aborted after exceeding MaxDuration ({MaxDuration}): {Accepted}/{Needed} headers " +
                    "found in {Attempts} attempts, {Remaining} candidates left untried.",
                    options.MaxDuration, accepted.Count, rowHeaders.Count, attempts, remaining.Count);
                throw new GridGenerationException(
                    $"Grid generation aborted after exceeding {options.MaxDuration} " +
                    $"(found {accepted.Count}/{rowHeaders.Count} valid headers in {attempts} attempts).");
            }

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

            if (!isValid)
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

    // REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
    // cache miss triggers a live lookup, persisted immediately (never
    // deferred/batched) as WikidataLookupOrigin.Sync — a routine query
    // against Wikidata's own vetted per-category intersection. As of
    // ADR-0032 this origin and REQ-211's narrower guess-time fallback below
    // both persist as "verified" (ADR-0029 had trusted only this one as
    // ground truth; ADR-0032 reversed that split), but the two origins are
    // still passed through distinctly for logging/future re-differentiation
    // — see ADR-0032. A category value with no resolved WikidataQid is not
    // an error — the live lookup just returns no matches (REQ-109), which
    // this treats as an ordinary 0-count, handled by the caller's normal
    // retry logic.
    private async Task<int> GetMatchCountAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        CancellationToken cancellationToken)
    {
        var cachedCount = await playerStoreRepository.CountPlayersWithBothAttributesAsync(
            MapAttributeType(rowCategoryType), row.Name, MapAttributeType(colCategoryType), col.Name, cancellationToken);
        if (cachedCount > 0)
            return cachedCount;

        var liveMatches = await LookupLiveMatchesAsync(
            rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
        return liveMatches?.Count ?? 0;
    }

    // Dispatches to whichever IWikidataLookupService method matches this
    // pairing — the single place that decision is made, shared by
    // GetMatchCountAsync (generation-time) and RefreshCellFromLiveLookupAsync
    // (REQ-211 guess-time fallback) so the two can't drift on which pairings
    // are handled. Returns null for a pairing neither method knows how to
    // resolve (e.g. Trophy x Trophy, which has no dedicated persist method)
    // — distinct from an empty list,
    // which means the pairing IS handled but Wikidata found no match.
    // WikidataLookupService only ever reads Name/WikidataQid off the
    // CountryDefinition/ClubDefinition it's given (never Id) — safe to
    // construct throwaway instances here rather than threading the real
    // reference-table rows through the whole candidate-picking pipeline
    // just for an Id nothing downstream uses. `origin` is passed through
    // as-is from whichever caller invoked this — see ADR-0032 for what it
    // (no longer) controls: both origins persist the same starting
    // Confidence now, but the value is still threaded through for
    // logging/future re-differentiation.
    private async Task<IReadOnlyList<Player>?> LookupLiveMatchesAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken)
    {
        if (rowCategoryType == CategoryPairingRules.Country && colCategoryType == CategoryPairingRules.Club)
        {
            // REQ-114/ADR-0035: row.UsesCountryForSportProperty threads
            // CategoryCandidate's copy of CountryDefinition's per-row query-
            // property flag through — LookupAndPersistAsync itself decides
            // P27 vs. P1532 from it, so this call site needs no pairing-
            // specific branching of its own.
            return await wikidataLookupService.LookupAndPersistAsync(
                new CountryDefinition { Name = row.Name, WikidataQid = row.WikidataQid, UsesCountryForSportProperty = row.UsesCountryForSportProperty },
                new ClubDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                origin,
                cancellationToken);
        }

        if (rowCategoryType == CategoryPairingRules.Club && colCategoryType == CategoryPairingRules.Club)
        {
            return await wikidataLookupService.LookupAndPersistClubClubAsync(
                new ClubDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                new ClubDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                origin,
                cancellationToken);
        }

        // S-031/REQ-108: SelectPairing always keeps Trophy as the *second*
        // type in a mixed pairing (Country/Club always first) — only these
        // three orderings are ever produced, never Trophy first.
        //
        // REQ-114/ADR-0035 scope note: unlike the Country x Club branch
        // above, row.UsesCountryForSportProperty is deliberately NOT
        // threaded through here — LookupAndPersistTrophyCountryAsync has no
        // P1532-aware counterpart to BuildTrophyCountryIntersectionQuery
        // yet, so a national-team country in a Country x Trophy pairing
        // would silently fall back to (wrong) P27 semantics if it reached
        // this branch. In practice it can't: SelectPairing's own comment
        // notes trophyCount(1) never clears any realistic grid `size`, so
        // this branch is unreachable in production today, same as Trophy x
        // Trophy below. Extending P1532 support to this pairing is
        // follow-up work for whenever the trophy pool actually grows enough
        // to make it reachable.
        if (rowCategoryType == CategoryPairingRules.Country && colCategoryType == CategoryPairingRules.Trophy)
        {
            return await wikidataLookupService.LookupAndPersistTrophyCountryAsync(
                new TrophyDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                new CountryDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                origin,
                cancellationToken);
        }

        if (rowCategoryType == CategoryPairingRules.Club && colCategoryType == CategoryPairingRules.Trophy)
        {
            return await wikidataLookupService.LookupAndPersistTrophyClubAsync(
                new TrophyDefinition { Name = col.Name, WikidataQid = col.WikidataQid },
                new ClubDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                origin,
                cancellationToken);
        }

        if (rowCategoryType == CategoryPairingRules.Trophy && colCategoryType == CategoryPairingRules.Trophy)
        {
            // Trophy x Trophy has no dedicated IWikidataLookupService method
            // (S-031 scoped the two new methods to Country/Club x Trophy
            // only, per docs/backlog.md — a live-lookup fallback for this
            // pairing is unreachable in practice anyway, see SelectPairing's
            // own comment on trophyCount(1) never clearing `size`). Falls
            // through to `return null` below, same as any other
            // not-yet-handled pairing — fails closed, never throws.
            return null;
        }

        return null;
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
