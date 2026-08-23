using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of GridGameModule —
// see IGridLiveLookupDispatcher's own doc comment for why this is its own
// class rather than living inside GridGenerationService or GridNameMatcher.
public class GridLiveLookupDispatcher(
    ICategoryValueRepository categoryValueRepository,
    IWikidataLookupService wikidataLookupService,
    IPlayerDataQualityRepository playerDataQualityRepository) : IGridLiveLookupDispatcher
{
    // REQ-211's Tier 0 fallback (ADR-0018) knows how to refresh a
    // Country x Club cell, a Club x Club cell (S-030), and, as of S-031, a
    // Country x Trophy or Club x Trophy cell — any other pairing (e.g.
    // Trophy x Trophy, which has no dedicated persist method — see
    // LookupMatchesAsync's own comment) can't be resolved from the
    // reference tables this way at all, and is left to fail closed via the
    // caller's existing cached-only result, same as a genuinely-incorrect
    // guess. Routes through the same LookupMatchesAsync dispatcher
    // GridGenerationService.GetMatchCountAsync uses during generation,
    // rather than a second, independently-written pairing check —
    // LookupMatchesAsync returns null for a pairing it doesn't handle,
    // which is exactly this method's fail-closed signal.
    public async Task<bool> TryRefreshCellAsync(GridCell cell, CancellationToken cancellationToken)
    {
        var row = await ResolveCandidateAsync(cell.RowCategoryType, cell.RowCategoryValue, cancellationToken);
        var col = await ResolveCandidateAsync(cell.ColCategoryType, cell.ColCategoryValue, cancellationToken);
        if (row is null || col is null)
            return false;

        // 2026-08-10 fix: PlayerCacheWarmingService already knows, from its
        // own independent runs, when this exact pair's Wikidata query
        // structurally fails/times out on 2+ consecutive runs
        // (PairLookupFailure, ADR-0052) - before this fix, a guess against
        // such a pair still paid the full guess-time-fallback timeout
        // (currently 28s) live, every single guess, only to end up at the
        // same LiveLookupUnavailableException below anyway. This is purely a
        // latency short-circuit: the pair is still genuinely UNKNOWN, not
        // "incorrect" (ADR-0046's guarantee is unchanged - no attempt is
        // consumed either way), it just skips a live call already known to
        // be doomed rather than waiting it out again. Only ever true for
        // Country×Club/Club×Club pairs - PlayerCacheWarmingService doesn't
        // track Trophy pairings (see its own WarmAsync scope), so this is a
        // guaranteed-false, effectively free read for those, never a false
        // positive that would wrongly skip a live check that could resolve.
        if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
                CategoryPairingRules.MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue,
                CategoryPairingRules.MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue,
                PlayerCacheWarmingService.PersistentFailureThreshold, cancellationToken))
        {
            throw new LiveLookupUnavailableException(
                $"Cell {cell.Id}'s category pair is a known persistent Wikidata lookup failure (ADR-0052) - skipping a repeat live call.");
        }

        IReadOnlyList<Player>? liveMatches;
        try
        {
            liveMatches = await LookupMatchesAsync(
                cell.RowCategoryType, row.Value, cell.ColCategoryType, col.Value,
                WikidataLookupOrigin.GuessTimeFallback, cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            // REQ-211 (2026-07-27 fix): a timeout here means this cell's
            // correctness is genuinely UNKNOWN, not "no match" — the
            // guess-time fallback asked WikidataClient to throw instead of
            // its usual swallow-to-[] (see WikidataLookupService's
            // throwOnTimeout comment), specifically so this case is
            // distinguishable from a real "Wikidata answered, found
            // nothing." GridLiveLookupDispatcher is the one place a
            // DataSync-specific exception is allowed to cross into Core's
            // cross-boundary contract (LiveLookupUnavailableException,
            // XGArcade.Core.Games) — Core itself never references
            // WikidataQueryException or anything else DataSync-specific
            // (ADR-0003). Left uncaught here, ScoreSubmissionAsync's caller
            // (GuessSubmissionService) would otherwise silently fall through
            // to the cache-only "incorrect" ScoreResult below and persist a
            // wasted attempt for a guess that might well be correct (this
            // bug bundle's reported "guessed Seedorf, got 'failed to
            // fetch', retried, got 'incorrect'" symptom).
            throw new LiveLookupUnavailableException(
                $"Live Wikidata lookup for cell {cell.Id} did not complete in time: {ex.Message}");
        }

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
            // ADR-0061: trophy.IsTeamTrophy threaded through, same as
            // country.UsesCountryForSportProperty above — see
            // CategoryCandidate's own doc comment.
            return trophy is null ? null : new CategoryCandidate(trophy.Name, trophy.WikidataQid, IsTeamTrophy: trophy.IsTeamTrophy);
        }

        return null;
    }

    // Dispatches to whichever IWikidataLookupService method matches this
    // pairing — the single place that decision is made, shared by
    // GridGenerationService.GetMatchCountAsync (generation-time) and this
    // class's own TryRefreshCellAsync (REQ-211 guess-time fallback) so the
    // two can't drift on which pairings are handled. Returns null for a
    // pairing neither method knows how to resolve (e.g. Trophy x Trophy,
    // which has no dedicated persist method) — distinct from an empty list,
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
    public async Task<IReadOnlyList<Player>?> LookupMatchesAsync(
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
        // REQ-114/ADR-0035/ADR-0061: row.UsesCountryForSportProperty and
        // col.IsTeamTrophy are now BOTH threaded through here, matching the
        // Country x Club branch above's pattern exactly —
        // LookupAndPersistTrophyCountryAsync's own dispatch (ADR-0061)
        // branches on both flags together, so this call site needs no
        // pairing-specific branching of its own, same precedent as Country x
        // Club. Before ADR-0061, row.UsesCountryForSportProperty was
        // deliberately NOT threaded through here (LookupAndPersistTrophyCountryAsync
        // had no P1532-aware counterpart yet, and the branch was unreachable
        // in production anyway with only one trophy seeded) — that gap is
        // now closed; do not silently drop this threading again.
        if (rowCategoryType == CategoryPairingRules.Country && colCategoryType == CategoryPairingRules.Trophy)
        {
            return await wikidataLookupService.LookupAndPersistTrophyCountryAsync(
                new TrophyDefinition { Name = col.Name, WikidataQid = col.WikidataQid, IsTeamTrophy = col.IsTeamTrophy },
                new CountryDefinition { Name = row.Name, WikidataQid = row.WikidataQid, UsesCountryForSportProperty = row.UsesCountryForSportProperty },
                origin,
                cancellationToken);
        }

        // ADR-0061: col.IsTeamTrophy threaded through the same way as the
        // Country x Trophy branch above — LookupAndPersistTrophyClubAsync's
        // own dispatch branches on it (no club-side P27-vs-P1532 style split
        // needed, see that method's own doc comment).
        if (rowCategoryType == CategoryPairingRules.Club && colCategoryType == CategoryPairingRules.Trophy)
        {
            return await wikidataLookupService.LookupAndPersistTrophyClubAsync(
                new TrophyDefinition { Name = col.Name, WikidataQid = col.WikidataQid, IsTeamTrophy = col.IsTeamTrophy },
                new ClubDefinition { Name = row.Name, WikidataQid = row.WikidataQid },
                origin,
                cancellationToken);
        }

        if (rowCategoryType == CategoryPairingRules.Trophy && colCategoryType == CategoryPairingRules.Trophy)
        {
            // Trophy x Trophy has no dedicated IWikidataLookupService method
            // (S-031 scoped the two new methods to Country/Club x Trophy
            // only, per docs/backlog.md — a live-lookup fallback for this
            // pairing remains unreachable in practice, see SelectPairing's
            // own comment: it needs trophyCount >= size * 2, which the
            // ADR-0061 trophy-pool expansion to 3 still doesn't clear).
            // Falls through to `return null` below, same as any other
            // not-yet-handled pairing — fails closed, never throws.
            return null;
        }

        return null;
    }
}
