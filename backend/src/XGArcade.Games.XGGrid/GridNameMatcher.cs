using Microsoft.Extensions.Logging;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of GridGameModule —
// owns REQ-207/208/209's name-resolution work plus REQ-216's wrong-guess
// identity resolution.
public class GridNameMatcher(
    IPlayerRepository playerRepository,
    IPlayerAliasRepository playerAliasRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IPlayerOverrideRepository playerOverrideRepository,
    IPlayerNameIndexRepository playerNameIndexRepository,
    ILogger<GridNameMatcher> logger,
    IWikidataClient? wikidataClient = null) : IGridNameMatcher
{
    // REQ-208's three-stage matching order — exact primary name, then
    // alias, then bounded fuzzy — each stage only runs if the previous one
    // resolved to zero candidates satisfying both of the cell's categories.
    // Each stage reuses FilterByCategoriesAsync/AcceptMatchAsync so REQ-209's
    // disambiguation rule (a single fitting candidate auto-accepted, more
    // than one triggers a disambiguation prompt) applies identically
    // regardless of which stage produced the candidates.
    //
    // chosenPlayerId (REQ-209/REQ-210): when set, this call is a
    // resubmission answering a disambiguation prompt raised by an earlier
    // call for the same attempt — the pipeline below still re-runs from
    // scratch (never trusting a cached "which stage matched" from that
    // earlier call, since data can change between the prompt and the
    // resubmission) and AcceptMatchAsync validates chosenPlayerId against
    // whichever stage's `matching` list this run actually produces.
    public async Task<ScoreResult> FindMatchAsync(
        GridCell cell, string normalizedName, Guid? chosenPlayerId, Guid instanceId, CancellationToken cancellationToken)
    {
        var exactCandidates = await playerRepository.GetPlayersByNormalizedFullNameAsync(normalizedName, cancellationToken);
        var matching = await FilterByCategoriesAsync(cell, exactCandidates, cancellationToken);

        if (matching.Count == 0)
        {
            // REQ-208: known aliases/stage names, matched via PlayerAlias —
            // an exact NormalizedAlias equality check, same normalization as
            // the primary-name path (PlayerNameNormalizer.Normalize applied
            // at persist time, WikidataLookupService.PersistMatchesAsync).
            var aliasCandidates = await playerAliasRepository.GetPlayersByNormalizedAliasAsync(normalizedName, cancellationToken);
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

        return await AcceptMatchAsync(cell, instanceId, matching, chosenPlayerId, cancellationToken);
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
            var satisfiesRow = await playerOverrideRepository.HasEffectiveAttributeAsync(
                candidate.Id, CategoryPairingRules.MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue, cancellationToken);
            if (!satisfiesRow)
                continue;

            var satisfiesCol = await playerOverrideRepository.HasEffectiveAttributeAsync(
                candidate.Id, CategoryPairingRules.MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue, cancellationToken);
            if (satisfiesCol)
                matching.Add(candidate);
        }

        return matching;
    }

    // REQ-209: exactly one fitting candidate is accepted automatically; more
    // than one raises a disambiguation prompt instead of guessing on the
    // player's behalf. Shared by every stage of FindMatchAsync above so this
    // rule can't drift between the exact/alias/fuzzy paths.
    //
    // chosenPlayerId fast path (REQ-209/REQ-210): when set, this is a
    // resubmission answering a prompt raised earlier in the same attempt —
    // skip straight to verifying that specific player is (a) among this
    // run's `matching` candidates for whichever stage produced them and
    // (b) therefore still satisfies both categories right now (membership in
    // a freshly-computed `matching` list proves both at once — never trust
    // the client-supplied id blindly). A chosenPlayerId that doesn't
    // validate — not in the matching set any more, or matching is empty —
    // is treated as an ordinary incorrect guess, never thrown, same
    // fail-closed discipline as every other guess-scoring edge case here.
    private async Task<ScoreResult> AcceptMatchAsync(
        GridCell cell, Guid instanceId, IReadOnlyList<Player> matching, Guid? chosenPlayerId, CancellationToken cancellationToken)
    {
        if (chosenPlayerId is not null)
        {
            var chosen = matching.FirstOrDefault(p => p.Id == chosenPlayerId.Value);
            return chosen is null
                ? new ScoreResult { IsCorrect = false }
                : new ScoreResult { IsCorrect = true, PlayerAnswerId = chosen.Id };
        }

        if (matching.Count == 0)
            return new ScoreResult { IsCorrect = false };

        if (matching.Count == 1)
            return new ScoreResult { IsCorrect = true, PlayerAnswerId = matching[0].Id };

        logger.LogInformation(
            "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +
            "showing a disambiguation prompt per REQ-209.",
            cell.Id, instanceId, matching.Count);

        var candidates = await BuildDisambiguationCandidatesAsync(cell, matching, cancellationToken);
        return new ScoreResult { IsCorrect = false, DisambiguationCandidates = candidates };
    }

    // REQ-209: builds one DisambiguationCandidate per fitting player, each
    // carrying their OTHER known PlayerAttribute values — excluding
    // whichever of the cell's own two categories every candidate already
    // satisfies (redundant to show again, since that's exactly what put
    // them all in `matching`). Ordered by Id for a deterministic response
    // shape, same tie-break precedent as REQ-204's grouping.
    private async Task<IReadOnlyList<DisambiguationCandidate>> BuildDisambiguationCandidatesAsync(
        GridCell cell, IReadOnlyList<Player> matching, CancellationToken cancellationToken)
    {
        var rowAttributeType = CategoryPairingRules.MapAttributeType(cell.RowCategoryType);
        var colAttributeType = CategoryPairingRules.MapAttributeType(cell.ColCategoryType);

        var attributesByPlayerId = await playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync(
            matching.Select(p => p.Id).ToList(), cancellationToken);

        var candidates = new List<DisambiguationCandidate>(matching.Count);
        foreach (var player in matching.OrderBy(p => p.Id))
        {
            var distinguishing = GetDistinguishingAttributeValues(
                cell, rowAttributeType, colAttributeType, attributesByPlayerId, player.Id);
            candidates.Add(new DisambiguationCandidate(player.Id, player.FullName, distinguishing));
        }

        return candidates;
    }

    // The non-redundant half of a matching player's attributes —
    // BuildDisambiguationCandidatesAsync's own doc comment explains why the
    // cell's two own categories are excluded here.
    private static IReadOnlyList<string> GetDistinguishingAttributeValues(
        GridCell cell, string rowAttributeType, string colAttributeType,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>> attributesByPlayerId, Guid playerId)
    {
        if (!attributesByPlayerId.TryGetValue(playerId, out var attributes))
            return [];

        return attributes
            .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&
                        !(a.AttributeType == colAttributeType && a.AttributeValue == cell.ColCategoryValue))
            .Select(a => a.AttributeValue)
            .Distinct()
            .ToList();
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
        var pool = await playerAttributeRepository.GetPlayersWithEitherAttributeAsync(
            CategoryPairingRules.MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue,
            CategoryPairingRules.MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue,
            cancellationToken);

        if (pool.Count == 0)
            return [];

        var aliasesByPlayerId = await playerAliasRepository.GetPlayerAliasesByPlayerIdsAsync(
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

    // REQ-216/ADR-0057: resolves the guessed player's canonical name (and,
    // independently, an optional photo) for a cell that has just locked with
    // its final guess still incorrect — called by GridGameModule's own
    // ResolveWrongGuessPlayerAsync exactly once, only when it has already
    // determined `locked && !scoreResult.IsCorrect` for THIS submission (see
    // IGameModule.ResolveWrongGuessPlayerAsync's own doc comment for the
    // full "when/how often" contract the caller enforces).
    //
    // PlayerNameIndex.FindByNormalizedNameAsync (COMP-10, ADR-0007) is the
    // ONLY thing that can confirm submittedName names a real player at all —
    // null here means REQ-216's "no identity to show" case, unchanged from
    // today. Its PrimaryName is also this method's guaranteed fallback name:
    // unlike the photo half below, resolving a canonical name never depends
    // on any live lookup succeeding.
    public async Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(string submittedName, CancellationToken cancellationToken)
    {
        var normalized = PlayerNameNormalizer.Normalize(submittedName);
        var nameIndexEntry = await playerNameIndexRepository.FindByNormalizedNameAsync(normalized, cancellationToken);
        if (nameIndexEntry is null)
            return null;

        // Cache-first, same exact-then-alias matching order FindMatchAsync
        // already uses above — a wrong-but-real guess may already have a
        // correctness-side Player row (FullName/PhotoUrl) cached from
        // resolving some OTHER cell's answer key, in which case no live
        // Wikidata round-trip is needed at all. Player.FullName is preferred
        // here over PlayerNameIndex.PrimaryName when both are available —
        // it's the same canonical-name source REQ-214's correct-guess reveal
        // already trusts.
        var cached = (await playerRepository.GetPlayersByNormalizedFullNameAsync(normalized, cancellationToken)).FirstOrDefault()
            ?? (await playerAliasRepository.GetPlayersByNormalizedAliasAsync(normalized, cancellationToken)).FirstOrDefault();
        if (cached is not null)
            return new WrongGuessPlayerInfo(cached.FullName, cached.PhotoUrl);

        // ADR-0057: Wikidata-only, never API-Football, never gated on any
        // ExternalApiUsage threshold — a cosmetic display lookup for a guess
        // already known to be wrong, not a correctness-critical retry. Any
        // failure (timeout, HTTP error, parse error) is caught and swallowed
        // right here — REQ-216 still requires showing PlayerNameIndex's own
        // PrimaryName in that case (see this method's own doc comment: the
        // name half never depends on this lookup succeeding), just with no
        // photo. wikidataClient is nullable purely so tests that don't care
        // about this path aren't forced to wire one up; production DI
        // (Program.cs) always supplies the real client.
        if (wikidataClient is not null)
        {
            var livePhoto = await TryLookupLivePhotoAsync(submittedName, cancellationToken);
            if (livePhoto is not null)
                return livePhoto;
        }

        return new WrongGuessPlayerInfo(nameIndexEntry.PrimaryName, null);
    }

    // Isolates the try/catch around ResolveWrongGuessPlayerAsync's optional
    // live photo lookup — same fail-closed behavior as before (swallow
    // WikidataQueryException, fall back to null so the caller shows
    // PlayerNameIndex's own PrimaryName with no photo), only called once the
    // caller has already confirmed wikidataClient is non-null.
    private async Task<WrongGuessPlayerInfo?> TryLookupLivePhotoAsync(string submittedName, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = await wikidataClient!.QueryPlayerPhotoByNameAsync(submittedName, cancellationToken);
            return lookup is null ? null : new WrongGuessPlayerInfo(lookup.FullName, lookup.PhotoUrl);
        }
        catch (WikidataQueryException ex)
        {
            logger.LogInformation(ex,
                "REQ-216/ADR-0057: Wikidata-only wrong-guess photo lookup failed — showing " +
                "PlayerNameIndex's canonical name with no photo, never fail-closed (no correctness " +
                "verdict left to compute for a guess already known to be wrong).");
            return null;
        }
    }
}
