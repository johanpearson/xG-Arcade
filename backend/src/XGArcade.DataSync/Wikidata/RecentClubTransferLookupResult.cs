namespace XGArcade.DataSync.Wikidata;

// S-188 (docs/backlog.md, Epic 26): WikidataClient.QueryRecentClubTransfersAsync's
// own return shape — arrivals (BuildRecentClubArrivalsQuery, pq:P580
// filtered) and departures (BuildRecentClubDeparturesQuery, pq:P582
// filtered) merged into ONE dictionary per QID, since every caller
// (RecentTransferSweepService.SweepAsync) treats a WikidataCareerStintEntry
// identically regardless of which of the two underlying queries produced
// it — both flow through the exact same
// PlayerCareerStintRefreshService.BuildNewStintsByPlayerId ->
// CareerStintReconciler.Reconcile reconciliation ADR-0091 already
// established (an arrival that matches no existing (ClubName, StartYear)
// row inserts; a departure that matches one completes it in place).
//
// PlayerNamesByQid is the union of both queries' ?playerLabel bindings,
// needed to get-or-create a brand-new Player row for an arrival this
// codebase has never seen before (see
// SparqlResponseParsers.ParseRecentClubTransferBindings' own doc comment
// for why this is captured here at all, unlike ParseCareerStintBindings'
// own output, which discards playerLabel since its own callers already
// know every player's name from an earlier pool-query pass).
public record RecentClubTransferLookupResult(
    IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> StintsByQid,
    IReadOnlyDictionary<string, string> PlayerNamesByQid);
