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
    ILogger<PlayerNameIndexImporter> logger)
{
    // WDQS has its own result-size/time limits regardless of this app's own
    // concerns (ADR-0011's evidence of 9-27s query times under load) — this
    // is a much larger, unfiltered result set than the per-cell intersection
    // queries (a handful to a few hundred rows), so a single unbounded query
    // is not an option here the way it deliberately is for those (see
    // WikidataClient.BuildCountryClubIntersectionQuery's own comment on why
    // THAT query has no LIMIT). 5,000 is comfortably inside WDQS's usual
    // per-request row ceiling while keeping the number of round-trips for a
    // "many thousands of players" pool manageable.
    public const int PageSize = 5000;

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var totalUpserted = 0;
        var offset = 0;

        // Loops until a page comes back empty — QueryPlayerPoolPageAsync
        // never throws (same "never blocks on a Wikidata failure" contract
        // as the intersection queries), so a transient WDQS failure on one
        // page surfaces as an empty page and this loop simply stops early
        // rather than looping forever or crashing the whole import. A
        // partial import (rather than an all-or-nothing one) is an accepted
        // trade-off for a manually re-run, idempotent job: re-running
        // ImportAsync later resumes coverage for players missed on an
        // earlier interrupted page, at the cost of re-fetching (harmlessly)
        // every page before it.
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await wikidataClient.QueryPlayerPoolPageAsync(offset, PageSize, cancellationToken);
            if (page.Count == 0)
                break;

            var entries = page.Select(ToIndexEntry).ToList();
            await repository.UpsertManyAsync(entries, cancellationToken);

            totalUpserted += entries.Count;
            logger.LogInformation(
                "import-player-name-index: upserted {PageCount} entries at offset {Offset} (running total {Total}).",
                entries.Count, offset, totalUpserted);

            offset += PageSize;
        }

        return totalUpserted;
    }

    private static PlayerNameIndex ToIndexEntry(WikidataNameIndexEntry entry) => new()
    {
        PlayerId = DeterministicPlayerId(entry.WikidataQid),
        PrimaryName = entry.FullName,
        NormalizedName = PlayerNameNormalizer.Normalize(entry.FullName),
        BirthYear = entry.BirthYear,
        PrimaryNationality = entry.Nationality,
        PhotoUrl = entry.PhotoUrl,
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
