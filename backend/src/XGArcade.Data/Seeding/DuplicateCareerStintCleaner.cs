using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
// ADR-0058): manually-triggered, one-off maintenance tool (Program.cs's
// `clean-duplicate-career-stints` CLI verb) for the ~608K-row
// PlayerCareerStint table's PRE-EXISTING cross-writer duplicates — rows
// persisted before this fix's canonicalization landed, where the same real
// stint was written twice under two different ClubName strings (once by
// WikidataLookupService.PersistCareerStintsAsync under the seeded
// ClubDefinition.Name, once by PlayerCareerStintRefreshService/
// PlayerCareerPrefetchService under Wikidata's own raw, only-suffix-
// normalized label). Every fetch AFTER this fix already canonicalizes by
// QID at write time and will never create a new instance of this duplicate
// shape — this class exists only to catch up already-materialized rows.
//
// Deliberately NOT a blind purge-and-reseed of the whole table, unlike
// StaleClubAttributeCleaner's own "wipe and let the next warm-player-cache/
// prefetch run repopulate" precedent for a wrong-QID incident. Two reasons:
//   1. There is no QID stored on an already-persisted PlayerCareerStint row
//      (only ClubName) to re-canonicalize against — the only way to know
//      whether a raw label like "Olympique Lyonnais" maps to a seeded
//      "Lyon" would be a fresh live Wikidata re-query, which this sandbox
//      cannot perform and which a full re-run of prefetch-player-careers
//      would require anyway.
//   2. A full purge of PlayerCareerStint would temporarily collapse
//      GetCareerStintCandidatePlayerIdsAsync's candidate pool down to
//      whatever ADR-0054's own per-round byproduct writes have accumulated
//      since the purge, for as long as a fresh prefetch-player-careers run
//      takes (a genuinely long WDQS-bound job, per NOTES.md 2026-08-02's
//      "60s batch timeout" tuning notes) — a real xG Path availability
//      regression, disproportionate for what is presently a COSMETIC bug
//      (an extra displayed club-reveal node for the same real stint, never
//      a scoring/correctness error: HasEffectiveAttributeAsync/xG Grid
//      never reads this table at all, per PlayerCareerStint's own doc
//      comment).
//
// Instead: a narrow, provable-only cleanup. A row is deleted ONLY when
// ANOTHER row for the exact same (PlayerId, StartYear, EndYear,
// AppearanceCount) tuple already exists whose ClubName IS a seeded
// ClubDefinition.Name — i.e., the canonical row for this exact real stint
// (same dates, same appearance count) is already present, so the
// non-canonical row is PROVABLY a duplicate of it without needing to know
// which specific alternate Wikidata label it was. A row at a genuinely
// unseeded club (no canonical counterpart exists at all) is never touched —
// correctly so, since NormalizeClubName's own comment already establishes
// why a broader fuzzy match is a correctness risk, not just here but there
// too. This mirrors the exact (ClubName, StartYear, EndYear,
// AppearanceCount) tuple both writers already dedup new stints against
// (WikidataLookupService.PersistCareerStintsAsync/
// PlayerCareerStintRefreshService.BuildNewStintsByPlayerId) — same key, run
// retroactively over already-persisted rows instead of at write time.
//
// Idempotent and safe to re-run: once the canonical/non-canonical pair for
// a stint has been reduced to just the canonical row, there's nothing left
// to match on a second run, same "safe to run again" contract as
// StaleClubAttributeCleaner/PairLookupFailureCleaner.
public static class DuplicateCareerStintCleaner
{
    // Full-table read, same order of magnitude as
    // IPlayerStoreRepository.GetUnseededClubCandidatesAsync's own diagnostic
    // scan of this table — acceptable for a one-off, manually-triggered CLI
    // verb, not a per-request path (REQ-1201's own perf fix, which this
    // class deliberately does NOT need to match, was about the per-round
    // generation hot path, not this tool).
    public static async Task<int> CleanAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var seededClubNames = (await dbContext.ClubDefinitions
                .Select(c => c.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // No seeded clubs at all means there is no canonical name to prove
        // any row against — almost certainly a wrong connection string or a
        // never-seeded database, same "fail loudly rather than print a
        // plausible 0" reasoning as StaleClubAttributeCleaner.CleanAllSeededClubsAsync.
        if (seededClubNames.Count == 0)
        {
            throw new InvalidOperationException(
                "clean-duplicate-career-stints found no ClubDefinition rows to canonicalize against — " +
                "is this the right database, and has migrate-and-seed run against it?");
        }

        var allStints = await dbContext.PlayerCareerStints.ToListAsync(cancellationToken);

        var canonicalKeys = allStints
            .Where(s => seededClubNames.Contains(s.ClubName))
            .Select(s => (s.PlayerId, s.StartYear, s.EndYear, s.AppearanceCount))
            .ToHashSet();

        var duplicates = allStints
            .Where(s => !seededClubNames.Contains(s.ClubName)
                && canonicalKeys.Contains((s.PlayerId, s.StartYear, s.EndYear, s.AppearanceCount)))
            .ToList();

        if (duplicates.Count == 0)
            return 0;

        dbContext.PlayerCareerStints.RemoveRange(duplicates);

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate it.
        await dbContext.SaveChangesAsync(cancellationToken);

        return duplicates.Count;
    }
}
