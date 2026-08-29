namespace XGArcade.DataSync.Wikidata;

// S-188 (docs/backlog.md, Epic 26 — Supabase free-tier egress remediation):
// a THIRD, narrower freshness mechanism alongside ADR-0088's "ever swept,
// skip forever" default and ADR-0090's rotating bounded resweep (both on
// PlayerCareerPrefetchService's own full player-pool sweep). This one is a
// cheap, targeted, DATE-FILTERED query per seeded club (P580/P582 qualifier
// FILTERs, server-side on WDQS) instead of a full player-pool re-fetch,
// meant to be run manually around a transfer-window deadline day rather
// than waiting out ADR-0090's multi-month rotation for that one club to
// come back around.
//
// Deliberately narrower in SCOPE than PlayerCareerPrefetchService, not just
// in query shape: this service only ever writes PlayerCareerStint rows (xG
// Path/COMP-11's own byproduct data, via the exact same
// PlayerCareerStintRefreshService.BuildNewStintsByPlayerId/
// CareerStintReconciler machinery ADR-0091 already established, reused
// verbatim, never reimplemented) — PLUS, as of S-189/ADR-0093, a
// PlayerAttribute/PlayerData row for each genuinely NEW arrival (xG
// Grid/COMP-06's own guess-correctness answer key — ADR-0007's
// autocomplete/correctness separation means only
// PlayerAttribute/PlayerOverride ever back a guess verdict). A departure
// still writes/removes nothing on PlayerAttribute — Grid's "ever played for
// this club" answer semantics mean a player who left is still correctly a
// valid answer forever, so only arrivals ever touch this table. See
// ADR-0093 for why this closes the "Grid-vs-Path freshness asymmetry"
// ADR-0092 originally left open, and for the targeted
// ConfirmedLowMatchPair/PairLookupFailure invalidation
// (IPlayerDataQualityRepository.ClearMatchPairAsync) that makes writing
// PlayerAttribute here safe. This service still deliberately does NOT touch
// CountryDefinition/ClubDefinition.PlayerPoolSweptAt at all — writing that
// column here would incorrectly tell ADR-0088's skip-forever check that
// this club's FULL pool was re-verified, when only a narrow recent-activity
// slice actually was. A freshly-transferred player becomes visible to both
// xG Path (career-stint clues, target eligibility) AND xG Grid (a valid
// guess answer for this club) via this service, well before ADR-0090's
// rotation would otherwise reach that club.
public interface IRecentTransferSweepService
{
    // lookbackDays must be positive — the cutoff threaded into both
    // BuildRecentClubArrivalsQuery's (pq:P580) and
    // BuildRecentClubDeparturesQuery's (pq:P582) FILTER clauses is
    // DateTime.UtcNow minus this many days, computed once per call and
    // reused for every seeded club.
    //
    // Never throws mid-run — a single club's fetch failure is logged and
    // the sweep continues with the rest, same "keep going, report at the
    // end" shape PlayerCareerPrefetchService.SweepAsync/PlayerNameIndexImporter
    // already use. Throws InvalidOperationException ONLY after every seeded
    // club with a resolved WikidataQid has been attempted, if anything
    // failed, so the CLI job/GitHub Actions run goes red — idempotent, safe
    // to re-run.
    Task<RecentTransferSweepResult> SweepAsync(int lookbackDays, CancellationToken cancellationToken = default);
}

// ClubsProcessed/ClubsFailed cover every seeded club with a resolved
// WikidataQid — a club with none is silently skipped, same REQ-109
// "unresolved QID isn't an error" convention PlayerCareerPrefetchService
// already applies. PlayersTouched/StintsAdded are the same "get-or-create +
// reconcile" totals PlayerCareerPrefetchResult already reports.
// StintsCompleted is new here (PlayerCareerPrefetchResult has no equivalent
// counter) — a departure this run resolves by filling in an existing row's
// EndYear/AppearanceCount via UpdateCareerStintCompletionsAsync (ADR-0091),
// not by inserting a new row, so it would otherwise be invisible in a
// StintsAdded-only summary. AttributesAdded (S-189) mirrors
// PlayerCareerPrefetchResult.AttributesAdded — every genuinely new arrival
// gets exactly one new PlayerAttribute row; a departure or an
// already-attributed arrival contributes 0.
public record RecentTransferSweepResult(
    int ClubsProcessed, int ClubsFailed, int PlayersTouched, int StintsAdded, int StintsCompleted, int AttributesAdded);
