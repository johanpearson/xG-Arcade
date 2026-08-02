namespace XGArcade.DataSync.Wikidata;

// ADR-0055: the bulk, proactive counterpart of PlayerCareerStintRefreshService
// (ADR-0054) — instead of refreshing a handful of already-selected xG Path
// targets, this sweeps every seeded CountryDefinition's full eligible player
// pool and writes their careers directly, independent of whatever xG Grid's
// own lookups have ever queried. This is what actually widens xG Path's
// candidate pool (XGPathGameModule.GetEligiblePlayerIdsAsync), not just
// enriches an already-chosen target — see ADR-0055's own scope note.
public interface IPlayerCareerPrefetchService
{
    // Never throws mid-run — a single country's (or a single career-fetch
    // batch's) failure is logged and the sweep continues with the rest, same
    // "keep going, report at the end" shape PlayerNameIndexImporter.ImportAsync
    // uses for a failed birth-year slice. Throws InvalidOperationException
    // ONLY after every country has been attempted, if anything failed — so
    // the CLI job/GitHub Actions run goes red (a signal to re-run; this
    // service is idempotent) without losing whatever succeeded.
    Task<PlayerCareerPrefetchResult> PrefetchAsync(CancellationToken cancellationToken = default);
}

public record PlayerCareerPrefetchResult(
    int CountriesProcessed, int PlayersTouched, int StintsAdded, int CountriesFailed, int CareerBatchesFailed);
