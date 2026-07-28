namespace XGArcade.Data.Entities;

// REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): the
// missing "checked, genuinely below MinValidAnswers, as of the current
// reference-data/query-shape state" marker PlayerCacheWarmingService.WarmAsync
// needs to stop re-querying ~1200 confirmed-low pairs on every run (1207/1214
// live-queried pairs in one measured run). A pair with a real non-zero
// match count already has that signal for free — its matches are persisted
// as ordinary PlayerAttribute rows, so CountPlayersWithBothAttributesAsync
// already distinguishes "checked, N < MinValidAnswers real matches" from
// "never checked" (N == 0 either way). The genuine gap is the zero-match
// case: WikidataLookupService's contract is that a query finding truly zero
// matches persists nothing at all (PersistMatchesAsync's early
// `if (matches.Count == 0) return [];`), so a confirmed-zero pair and a
// never-checked pair are otherwise indistinguishable. This table closes that
// gap for BOTH cases uniformly (see PlayerCacheWarmingService.WarmAsync's own
// comment for why marking the non-zero case too, even though it's already
// distinguishable via CountPlayersWithBothAttributesAsync, is harmless and
// keeps the skip logic in one place).
//
// A new table (not a new column on PlayerAttribute/Player) — deliberately,
// because a confirmed-low pair usually has NO corresponding PlayerAttribute
// rows to hang a column off (the zero-match case, the one this table
// actually exists for). A composite-key row here, one per checked pair, is
// the natural shape: (FirstAttributeType, FirstAttributeValue,
// SecondAttributeType, SecondAttributeValue) mirrors
// IPlayerStoreRepository.CountPlayersWithBothAttributesAsync's own parameter
// shape exactly, since that's the read this table's presence/absence
// short-circuits. Ordering follows each call site's own convention
// (PlayerCacheWarmingService always passes nationality-then-club for
// Country x Club, and clubs[i]-then-clubs[j] for Club x Club) — safe because
// every check and every write goes through that same one call site, so the
// two "sides" are never transposed against each other.
//
// Invalidation (the hard invariant REQ-110's text calls out): this table is
// deliberately NOT self-expiring — nothing here knows when reference data or
// a query shape changes. It is instead cleared by the same tools that
// already force a full re-check after such a change:
// StaleClubAttributeCleaner (REQ-111, both named-club and --all-clubs modes)
// and the `purge-player-pool` CLI verb (REQ-112/S-038, full unscoped reset).
// See those two call sites for the actual clearing logic — this entity
// itself has no opinion on when it goes stale, only that something checked
// this pair once and found it below MinValidAnswers.
public class ConfirmedLowMatchPair
{
    public required string FirstAttributeType { get; set; }
    public required string FirstAttributeValue { get; set; }
    public required string SecondAttributeType { get; set; }
    public required string SecondAttributeValue { get; set; }

    // The real match count observed at confirmation time (0 for the
    // genuine-zero case) — not read by the skip-check itself (presence of
    // the row is the only signal that matters), kept purely for operator
    // diagnostics (e.g. "why is this pair marked low — was it 0 or 4?").
    public int MatchCount { get; set; }

    public DateTime ConfirmedAt { get; set; }
}
