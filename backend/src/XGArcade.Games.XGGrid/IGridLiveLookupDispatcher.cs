using XGArcade.Data.Entities;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of the original
// GridGameModule — the single place a category-pairing live Wikidata lookup
// is dispatched, shared by generation-time cache-miss resolution
// (GridGenerationService.GetMatchCountAsync) and REQ-211's guess-time
// fallback (this interface's own TryRefreshCellAsync). Kept as its own
// class, distinct from both GridGenerationService and GridNameMatcher,
// specifically because both of those classes call into it — putting it on
// either one would make the other depend on it in a way that isn't its own
// concern.
public interface IGridLiveLookupDispatcher
{
    // Dispatches to whichever IWikidataLookupService method matches this
    // pairing — see the implementation's own doc comment for the full
    // "why one dispatch point" rationale. Returns null for a pairing this
    // dispatcher doesn't know how to resolve (e.g. Trophy x Trophy, which
    // has no dedicated persist method) — distinct from an empty list, which
    // means the pairing IS handled but Wikidata found no match.
    Task<IReadOnlyList<Player>?> LookupMatchesAsync(
        string rowCategoryType, CategoryCandidate row,
        string colCategoryType, CategoryCandidate col,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken);

    // REQ-211's Tier 0 fallback (ADR-0018): re-runs a specific cell's own
    // row/col intersection live, persisting immediately, so a guess that
    // didn't already resolve from cache gets one more chance before staying
    // "incorrect." False means the pairing isn't one this dispatcher can
    // resolve (fails closed, same as an ordinary incorrect guess) — never
    // thrown for that case. May throw
    // XGArcade.Core.Games.LiveLookupUnavailableException when the cell's
    // pair is a known persistent Wikidata lookup failure (ADR-0052) or when
    // the live lookup itself times out — see the implementation's own doc
    // comment for both exact conditions.
    Task<bool> TryRefreshCellAsync(GridCell cell, CancellationToken cancellationToken);
}
