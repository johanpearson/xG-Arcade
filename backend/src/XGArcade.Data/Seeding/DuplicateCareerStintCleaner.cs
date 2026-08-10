using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
// ADR-0059): manually-triggered, one-off maintenance tool (Program.cs's
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
//
// Widened, and a second pass added (2026-08-10, bug-bundle; see
// docs/decisions/0063-duplicate-career-stint-cleaner-appearance-count-merge-widening.md
// for the ADR this widening required, per ADR-0059's own "For AI agents"
// guardrail against widening this class's matching without a fresh ADR):
// the exact (PlayerId, StartYear, EndYear, AppearanceCount) tuple two
// paragraphs up is no longer applied literally. See CleanAsync's own Step
// 1/Step 2 comments for the full detail, but in short: (a) a null
// AppearanceCount on one side and a populated value on the other now still
// counts as "provably the same stint" — a null means "unknown," not a
// genuinely different number, and (b) a second pass now also merges rows
// that share the exact same ClubName (seeded or not), which the original
// single pass never compared against itself. Both changes preserve the one
// non-negotiable carve-out: two rows with DIFFERENT, both-POPULATED
// AppearanceCount values are still never merged, seeded-name match or not.
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

        // Not .AsNoTracking(): a Step 1 merge (below) can mutate a
        // surviving canonical row's AppearanceCount in place, and that
        // change needs to be tracked for the single SaveChangesAsync call
        // at the end to persist it.
        var allStints = await dbContext.PlayerCareerStints.ToListAsync(cancellationToken);
        var toRemove = new HashSet<PlayerCareerStint>();

        // ---- Step 1: cross-writer name-variant duplicates (this class's
        // original purpose — see the class doc comment above) ----
        // Bug fix (2026-08-10, bug-bundle): widened to also treat an
        // AppearanceCount of null on one side and a populated value on the
        // other as PROVING the same real stint, not just an exact match —
        // see WikidataClient.MergeCareerStintEntries' identical rule, which
        // this retroactive cleanup mirrors so the go-forward (live parse)
        // and existing-data paths converge on the same behavior. A null
        // AppearanceCount means "unknown," and a populated value observed
        // on the matching row is strictly more informative, never a
        // conflict — so the surviving canonical row's AppearanceCount is
        // updated to the populated value before the non-canonical row is
        // removed, rather than the more informative value being silently
        // dropped along with the row that carried it. Two rows with
        // DIFFERENT, both-populated AppearanceCounts are still left alone
        // entirely — the deliberate correctness-risk carve-out this
        // class's own doc comment already establishes, unchanged: could
        // plausibly be two genuinely different stints (e.g. a
        // loan-and-return spell), so this is a narrower, intentional
        // non-fix, not an oversight.
        var canonicalRowsByKey = allStints
            .Where(s => seededClubNames.Contains(s.ClubName))
            .GroupBy(s => (s.PlayerId, s.StartYear, s.EndYear))
            .ToDictionary(g => g.Key, g => g.ToList());

        var nonCanonicalRowsByKey = allStints
            .Where(s => !seededClubNames.Contains(s.ClubName))
            .GroupBy(s => (s.PlayerId, s.StartYear, s.EndYear))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Bug fix (2026-08-10 follow-up, ADR-0063, quality-gate finding):
        // grouped by key up front (rather than looping per-stint with an
        // inline TryGetValue, as before) so a 3+-row group sharing a key can
        // be reasoned about as a whole instead of one pairwise mutation at a
        // time. The previous per-stint loop mutated a canonical row's
        // AppearanceCount in place while iterating, and a LATER
        // non-canonical stint for the SAME key read that already-mutated
        // value back out via FirstOrDefault — so for a group of 3+ rows
        // (e.g. canonical row null, one non-seeded row 25, another
        // non-seeded row 95), which value the canonical row ended up with
        // (and therefore which non-seeded row failed to match and stayed
        // unmerged) depended on allStints' enumeration order: undocumented,
        // untested, and not something to rely on over production's
        // ~608K-row table.
        foreach (var (key, nonCanonicalRows) in nonCanonicalRowsByKey)
        {
            if (!canonicalRowsByKey.TryGetValue(key, out var canonicalRows))
                continue;

            // More than one canonical (seeded-name) row sharing this exact
            // key is a rare, untested edge case (e.g. two distinct seeded
            // clubs with identical start/end years) — which one is "the"
            // canonicalization target is itself ambiguous, so leave the
            // whole group alone rather than guess, same conservative
            // instinct as the ambiguous-AppearanceCount case below.
            if (canonicalRows.Count != 1)
                continue;

            var canonical = canonicalRows[0];

            if (nonCanonicalRows.Count > 1)
            {
                // 3+-row group (this canonical row plus 2+ non-canonical
                // rows sharing the same key). Made deterministic and
                // conservative: if more than one DISTINCT populated
                // AppearanceCount exists anywhere across the whole group
                // (canonical row included), leave every row in the group
                // untouched — the same both-populated-and-different
                // carve-out the 2-row case below already applies,
                // generalized to N rows instead of silently picking a
                // winner via mutation order. Only the unambiguous case (at
                // most one distinct populated AppearanceCount across the
                // whole group) is auto-merged.
                var distinctPopulatedAcrossGroup = new[] { canonical }
                    .Concat(nonCanonicalRows)
                    .Where(r => r.AppearanceCount is not null)
                    .Select(r => r.AppearanceCount!.Value)
                    .Distinct()
                    .ToList();

                if (distinctPopulatedAcrossGroup.Count > 1)
                    continue; // Ambiguous — leave every row for this key alone.

                if (distinctPopulatedAcrossGroup.Count == 1 && canonical.AppearanceCount is null)
                    canonical.AppearanceCount = distinctPopulatedAcrossGroup[0];

                foreach (var row in nonCanonicalRows)
                    toRemove.Add(row);

                continue;
            }

            // Exactly one canonical row and one non-canonical row share this
            // key — original pairwise rule, unchanged: a null
            // AppearanceCount on one side and a populated value on the
            // other still counts as "provably the same stint" (null means
            // "unknown," not a genuinely different number); two DIFFERENT,
            // both-populated AppearanceCounts are left unmerged entirely —
            // the deliberate correctness-risk carve-out this class's own
            // doc comment establishes.
            var stint = nonCanonicalRows[0];
            var isProvablySameStint = canonical.AppearanceCount == stint.AppearanceCount
                || canonical.AppearanceCount is null
                || stint.AppearanceCount is null;

            if (!isProvablySameStint)
                continue;

            if (canonical.AppearanceCount is null && stint.AppearanceCount is not null)
                canonical.AppearanceCount = stint.AppearanceCount;

            toRemove.Add(stint);
        }

        // ---- Step 2 (new, 2026-08-10 bug-bundle): same-ClubName
        // duplicates ----
        // Step 1 only ever proves a duplicate across a SEEDED name and a
        // DIFFERENT, non-seeded raw label. It never compares two rows that
        // already share the exact same ClubName (seeded or not) — which is
        // exactly the shape of the reported bug: "AC Milan 25 apps" and
        // "AC Milan 95 apps," "Real Sociedad 2 apps" and bare "Real
        // Sociedad," all under one identical, already-canonical ClubName
        // string. Group by (PlayerId, ClubName, StartYear, EndYear) and
        // apply the same null-vs-populated merge rule as Step 1: exactly
        // one distinct populated AppearanceCount in the group merges every
        // null-AppearanceCount row in it away (informationally subsumed);
        // more than one distinct populated value is left untouched
        // entirely (same correctness-risk carve-out). Rows already marked
        // for removal by Step 1 are excluded so they aren't double-counted.
        var remaining = allStints.Where(s => !toRemove.Contains(s));
        foreach (var group in remaining.GroupBy(s => (s.PlayerId, s.ClubName, s.StartYear, s.EndYear)))
        {
            var rows = group.ToList();
            if (rows.Count < 2)
                continue;

            var distinctPopulatedCounts = rows
                .Where(r => r.AppearanceCount is not null)
                .Select(r => r.AppearanceCount!.Value)
                .Distinct()
                .ToList();

            if (distinctPopulatedCounts.Count != 1)
                continue; // 0 populated (nothing to merge) or >1 distinct (correctness-risk carve-out) — leave alone.

            // Bug fix (2026-08-10 follow-up, ADR-0063, quality-gate finding):
            // the previous version of this branch only ever removed rows
            // where AppearanceCount was NULL, so a group with the SAME
            // populated AppearanceCount on every row (e.g. two identical
            // "25 apps" rows for the same club/dates, no null row at all)
            // passed the distinctPopulatedCounts check above but had
            // nothing for the old "remove null rows" loop to act on — both
            // rows silently survived untouched, contradicting this step's
            // own comment that it mirrors
            // WikidataClient.MergeCareerStintEntries's rule (which
            // correctly collapses to ONE row via `rows[0] with {...}`
            // whenever distinctPopulatedCounts.Count == 1, independent of
            // whether any row is null). Now collapses the whole group to a
            // single surviving row carrying the one populated value in
            // every case. Survivor choice: PlayerCareerStint has no
            // secondary identity field (e.g. no ClubQid, unlike
            // WikidataCareerStintEntry) to prefer between rows that already
            // agree, so ties are broken deterministically by preferring a
            // row that already holds the populated value (no mutation
            // needed) and otherwise falling back to the group's first row
            // (mutated in place), matching Step 1's mutate-then-keep
            // pattern above.
            var populatedCount = distinctPopulatedCounts[0];
            var survivor = rows.FirstOrDefault(r => r.AppearanceCount == populatedCount) ?? rows[0];
            if (survivor.AppearanceCount is null)
                survivor.AppearanceCount = populatedCount;

            foreach (var row in rows.Where(r => r != survivor))
                toRemove.Add(row);
        }

        if (toRemove.Count == 0)
            return 0;

        dbContext.PlayerCareerStints.RemoveRange(toRemove);

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate
        // it. One call covers both the removals above and any Step 1
        // AppearanceCount update made to a surviving tracked row.
        await dbContext.SaveChangesAsync(cancellationToken);

        return toRemove.Count;
    }
}
