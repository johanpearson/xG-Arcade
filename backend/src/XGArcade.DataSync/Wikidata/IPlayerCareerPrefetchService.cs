namespace XGArcade.DataSync.Wikidata;

// ADR-0055: the bulk, proactive counterpart of PlayerCareerStintRefreshService
// (ADR-0054) — instead of refreshing a handful of already-selected xG Path
// targets, this sweeps every seeded CountryDefinition's full eligible player
// pool and writes their careers directly, independent of whatever xG Grid's
// own lookups have ever queried. This is what actually widens xG Path's
// candidate pool (XGPathGameModule.GetEligiblePlayerIdsAsync), not just
// enriches an already-chosen target — see ADR-0055's own scope note.
// ADR-0069: also sweeps every seeded ClubDefinition's full eligible player
// pool (P54), independent of nationality — see that ADR for why the
// nationality-only scope above needed widening.
public interface IPlayerCareerPrefetchService
{
    // Never throws mid-run — a single country's/club's (or a single
    // career-fetch batch's) failure is logged and the sweep continues with
    // the rest, same "keep going, report at the end" shape
    // PlayerNameIndexImporter.ImportAsync uses for a failed birth-year
    // slice. Throws InvalidOperationException ONLY after every country AND
    // every club has been attempted, if anything failed — so the CLI
    // job/GitHub Actions run goes red (a signal to re-run; this service is
    // idempotent) without losing whatever succeeded.
    Task<PlayerCareerPrefetchResult> PrefetchAsync(CancellationToken cancellationToken = default);
}

// ADR-0069: CountriesProcessed/CountriesFailed cover the original
// nationality-scoped sweep unchanged; ClubsProcessed/ClubsFailed cover the
// new, additional club-scoped sweep. PlayersTouched/StintsAdded/
// CareerBatchesFailed are combined totals across both sweeps — a player
// discovered via nationality and a player discovered via club membership
// both flow through the same FetchAndPersistBatchAsync/AddCareerStintsBatchAsync
// path, so splitting those three by source would require plumbing a
// same-run "which sweep found this player first" distinction that nothing
// downstream needs (a player/stint is a player/stint regardless of which
// sweep's pool happened to include it first).
public record PlayerCareerPrefetchResult(
    int CountriesProcessed, int PlayersTouched, int StintsAdded, int CountriesFailed, int CareerBatchesFailed,
    int ClubsProcessed = 0, int ClubsFailed = 0);
