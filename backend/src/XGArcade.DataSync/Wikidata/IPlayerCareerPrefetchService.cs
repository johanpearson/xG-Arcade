namespace XGArcade.DataSync.Wikidata;

// ADR-0055: the bulk, proactive counterpart of PlayerCareerStintRefreshService
// (ADR-0054) — instead of refreshing a handful of already-selected xG Path
// targets, this sweeps every seeded CountryDefinition's full eligible player
// pool and writes their careers directly, independent of whatever xG Grid's
// own lookups have ever queried. This is what actually widens xG Path's
// candidate pool (PathEligibilityService.GetEligiblePlayerIdsAsync), not just
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
    //
    // REQ-110/S-187 (rotating bounded re-sweep, follow-up to ADR-0088):
    // maxEntitiesToResweep null (the default) keeps ADR-0088's exact
    // "ever swept, skip forever" behavior unchanged — this is what the
    // manual workflow_dispatch-only trigger on prefetch-player-careers.yml
    // keeps using (explicit "sweep everything not-yet-done" escape hatch
    // after a purge or reference-data change). A non-null N additionally
    // re-sweeps up to N entities whose pool was ALREADY swept (oldest
    // PlayerPoolSweptAt first), on top of every never-swept entity (always
    // swept, uncapped by N) — see PlayerCareerPrefetchService.SweepAsync's
    // own comment for the exact selection shape. ADR-0088 stopped a
    // transferred-in player from ever surfacing in an already-swept
    // country's/club's pool again; this bounded rotation is what gives that
    // player a path back in, a few entities at a time, without paying for a
    // full unbounded re-sweep.
    Task<PlayerCareerPrefetchResult> PrefetchAsync(
        int? maxEntitiesToResweep = null, CancellationToken cancellationToken = default);
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
//
// REQ-110 follow-up (2026-08-18): AttributesAdded is the same kind of
// combined-across-both-sweeps total as PlayersTouched/StintsAdded above —
// how many new PlayerAttribute rows (nationality from the country loop,
// club from the club loop) this run added. Defaulted to 0 for the same
// backward-compatibility reason ClubsProcessed/ClubsFailed were (ADR-0069):
// any existing positional-constructor call site that predates this field
// keeps compiling unchanged.
//
// REQ-110/ADR-0088/S-186 (2026-08-25): CountriesSkipped/ClubsSkipped count
// rows this run skipped entirely because PlayerPoolSweptAt was already
// non-null (a genuinely complete sweep from a prior run) — distinct from
// CountriesProcessed/ClubsProcessed, which only count rows this run
// actually fetched from Wikidata. Defaulted to 0 for the same
// backward-compatibility reason as AttributesAdded above.
public record PlayerCareerPrefetchResult(
    int CountriesProcessed, int PlayersTouched, int StintsAdded, int CountriesFailed, int CareerBatchesFailed,
    int ClubsProcessed = 0, int ClubsFailed = 0, int AttributesAdded = 0,
    int CountriesSkipped = 0, int ClubsSkipped = 0);
