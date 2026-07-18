using Microsoft.Extensions.Logging;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// S-032 (ADR-0007/REQ-207): the bulk name-index refresh job behind
// Program.cs's `import-player-name-index` CLI verb (ADR-0024: a long-running
// bulk job is a CLI verb dispatched via a GitHub Actions workflow, never a
// fire-and-forget background task — this Container App's `minReplicas: 0`
// would silently kill one mid-run — and never a long HTTP endpoint — the
// ingress times out around 240s, and this job is expected to run far longer
// than that against the full player-pool query). Run manually/periodically
// per ADR-0007's own follow-up note (start with a manual/periodic refresh,
// tighten only if names are noticeably missing) — import-player-name-index.yml
// is workflow_dispatch-only, same as warm-player-cache.yml, not on a cron.
//
// Deliberately placed in XGArcade.DataSync (next to WikidataLookupService),
// NOT in XGArcade.Data/Seeding alongside ReferenceDataSeeder/
// StaleClubAttributeCleaner as this story's originating task described —
// XGArcade.Data has no project reference to XGArcade.DataSync (only the
// reverse: XGArcade.DataSync.csproj references XGArcade.Data.csproj), so a
// class combining IWikidataClient with IPlayerNameIndexRepository cannot
// live in XGArcade.Data without a circular project reference, which .NET's
// build simply refuses. WikidataLookupService is the exact existing
// precedent for "a service that needs both a DataSync client and a Data
// repository lives in DataSync" — this class follows that same shape.
//
// PlayerNameIndex intentionally has no WikidataQid column (see its own doc
// comment and implementation-document.md §5's entity sketch, which this
// import follows exactly). Deriving PlayerId as a deterministic hash of the
// QID (rather than a fresh Guid.NewGuid() per run) is what makes re-running
// this import idempotent per player: IPlayerNameIndexRepository
// .UpsertManyAsync is keyed on PlayerId, so a random Guid on every run would
// insert a duplicate row per player on every re-import instead of correcting
// the existing one in place — the same "correct in place, don't just
// blindly insert" discipline ReferenceDataSeeder.SeedAsync's own doc comment
// establishes for ClubDefinition/CountryDefinition (keyed there by the
// natural (Name) key instead, since ClubDefinition's key isn't a Guid).
public class PlayerNameIndexImporter(
    IWikidataClient wikidataClient,
    IPlayerNameIndexRepository repository,
    ILogger<PlayerNameIndexImporter> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? retryBackoff = null)
{
    // Revised 2026-07-18: iterates one bounded birth-year slice at a time
    // (1939 → current year) instead of LIMIT/OFFSET paging — the original
    // paged query's ORDER BY over the whole unfiltered pool hit WDQS's hard
    // ~60s server-side timeout on EVERY page, the swallow-to-[] client
    // contract turned that into a phantom "end of data," and the job exited
    // 0 having imported nothing (NOTES.md 2026-07-18). Two consequences,
    // both deliberate:
    // - a genuinely empty year (real for sparse early years) just continues;
    // - a slice that still fails after MaxAttemptsPerYear retries is
    //   recorded, the remaining years still run (each successful slice is
    //   upserted immediately, so a re-run only has to redo the failed ones),
    //   and ImportAsync then THROWS so the CLI job / GitHub Actions run goes
    //   red. Silent partial import is no longer an accepted trade-off — the
    //   job is idempotent and manually re-run, so failing loudly and
    //   re-running is strictly better than "exit 0, imported 0."
    public const int MaxAttemptsPerYear = 3;

    // TimeProvider only bounds the year range (a CLI job, so
    // TimeProvider.System by default — Program.cs's verb block passes
    // nothing); optional ctor params (same style as WikidataClient's
    // queryTimeout) so tests can pin the current year and zero the backoff.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _retryBackoff = retryBackoff ?? TimeSpan.FromSeconds(5);

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var totalUpserted = 0;
        var failedYears = new List<int>();
        var currentYear = _timeProvider.GetUtcNow().Year;

        for (var year = WikidataClient.FirstEligibleBirthYear; year <= currentYear; year++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slice = await QuerySliceWithRetryAsync(year, cancellationToken);
            if (slice is null)
            {
                failedYears.Add(year);
                continue;
            }

            if (slice.Count == 0)
                continue; // A sparse year with no eligible players — normal, not a failure.

            // A player with two P569 statements in different years appears
            // in two slices — the deterministic PlayerId below makes the
            // second slice's upsert update the first's row in place rather
            // than duplicating it.
            var entries = slice.Select(ToIndexEntry).ToList();
            await repository.UpsertManyAsync(entries, cancellationToken);

            totalUpserted += entries.Count;
            logger.LogInformation(
                "import-player-name-index: upserted {SliceCount} entries for birth year {BirthYear} (running total {Total}).",
                entries.Count, year, totalUpserted);
        }

        if (failedYears.Count > 0)
        {
            throw new InvalidOperationException(
                $"import-player-name-index: {failedYears.Count} birth-year slice(s) failed after " +
                $"{MaxAttemptsPerYear} attempts each ({string.Join(", ", failedYears)}). " +
                $"{totalUpserted} entries were still upserted from the successful slices; " +
                "the job is idempotent — re-run it to fill in the failed years.");
        }

        return totalUpserted;
    }

    // Returns null when the slice failed all attempts (the caller records
    // the year and fails the run at the end) — never null for an empty
    // year, which is a successful [] result.
    private async Task<IReadOnlyList<WikidataNameIndexEntry>?> QuerySliceWithRetryAsync(
        int birthYear, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerYear; attempt++)
        {
            try
            {
                return await wikidataClient.QueryPlayerPoolBirthYearAsync(birthYear, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                if (attempt < MaxAttemptsPerYear)
                {
                    var backoff = _retryBackoff * attempt;
                    logger.LogWarning(ex,
                        "import-player-name-index: birth year {BirthYear} failed (attempt {Attempt}/{MaxAttempts}); retrying in {Backoff}.",
                        birthYear, attempt, MaxAttemptsPerYear, backoff);
                    await Task.Delay(backoff, cancellationToken);
                }
                else
                {
                    logger.LogError(ex,
                        "import-player-name-index: birth year {BirthYear} failed all {MaxAttempts} attempts; continuing with the remaining years, but this run WILL fail at the end.",
                        birthYear, MaxAttemptsPerYear);
                }
            }
        }

        return null;
    }

    private static PlayerNameIndex ToIndexEntry(WikidataNameIndexEntry entry) => new()
    {
        PlayerId = DeterministicPlayerId(entry.WikidataQid),
        PrimaryName = entry.FullName,
        NormalizedName = PlayerNameNormalizer.Normalize(entry.FullName),
        BirthYear = entry.BirthYear,
        PrimaryNationality = entry.Nationality,
    };

    // MD5's 16-byte digest maps directly onto a Guid's 16-byte layout — the
    // same shape as RFC 4122 v3/v5 name-based UUIDs, without pulling in a
    // dedicated library for it. Not a cryptographic use — collision
    // resistance against a deliberate adversary is irrelevant here, only
    // stability (the same QID always produces the same Guid) matters, so a
    // faster non-cryptographic hash would do just as well, but MD5 is
    // already in the BCL and exactly 16 bytes with no extra work needed.
    private static Guid DeterministicPlayerId(string wikidataQid) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"wikidata-player:{wikidataQid}")));
}
