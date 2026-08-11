using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerDataQualityRepository(XGArcadeDbContext dbContext) : IPlayerDataQualityRepository
{
    public async Task<bool> IsConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default) =>
        await dbContext.ConfirmedLowMatchPairs
            .AsNoTracking()
            .AnyAsync(c =>
                c.FirstAttributeType == firstAttributeType && c.FirstAttributeValue == firstAttributeValue &&
                c.SecondAttributeType == secondAttributeType && c.SecondAttributeValue == secondAttributeValue,
                cancellationToken);

    public async Task RecordConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int matchCount, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.ConfirmedLowMatchPairs.FirstOrDefaultAsync(c =>
            c.FirstAttributeType == firstAttributeType && c.FirstAttributeValue == firstAttributeValue &&
            c.SecondAttributeType == secondAttributeType && c.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        var confirmedAt = DateTime.UtcNow;

        if (existing is not null)
        {
            // Re-confirmation of an already-marked pair (see this method's
            // own interface doc comment) — update in place rather than
            // inserting a duplicate composite-key row.
            existing.MatchCount = matchCount;
            existing.ConfirmedAt = confirmedAt;
        }
        else
        {
            dbContext.ConfirmedLowMatchPairs.Add(new ConfirmedLowMatchPair
            {
                FirstAttributeType = firstAttributeType,
                FirstAttributeValue = firstAttributeValue,
                SecondAttributeType = secondAttributeType,
                SecondAttributeValue = secondAttributeValue,
                MatchCount = matchCount,
                ConfirmedAt = confirmedAt,
            });
        }

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync/upsert — the InMemory test provider can't
        // translate either.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsPersistentTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int threshold, CancellationToken cancellationToken = default) =>
        await dbContext.PairLookupFailures
            .AsNoTracking()
            .AnyAsync(f =>
                f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
                f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue &&
                f.ConsecutiveFailureCount >= threshold,
                cancellationToken);

    public async Task RecordTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PairLookupFailures.FirstOrDefaultAsync(f =>
            f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
            f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        var failedAt = DateTime.UtcNow;

        if (existing is not null)
        {
            existing.ConsecutiveFailureCount++;
            existing.LastFailedAt = failedAt;
        }
        else
        {
            dbContext.PairLookupFailures.Add(new PairLookupFailure
            {
                FirstAttributeType = firstAttributeType,
                FirstAttributeValue = firstAttributeValue,
                SecondAttributeType = secondAttributeType,
                SecondAttributeValue = secondAttributeValue,
                ConsecutiveFailureCount = 1,
                LastFailedAt = failedAt,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PairLookupFailures.FirstOrDefaultAsync(f =>
            f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
            f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        if (existing is null)
            return;

        dbContext.PairLookupFailures.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnseededClubCandidate>> GetUnseededClubCandidatesAsync(
        int top, CancellationToken cancellationToken = default)
    {
        // Case-insensitive comparison against ClubDefinition.Name — a
        // Wikidata-sourced PlayerCareerStint.ClubName (a P54 qualifier
        // label) and a hand-seeded ClubDefinition.Name come from two
        // different paths and could plausibly differ only in case even when
        // they denote the same club; an exact-case comparison would then
        // wrongly surface an already-seeded club as a "gap." This is
        // diagnostic-only output for a human to review — if this
        // normalization assumption ever hides a genuinely distinct club
        // that happens to share a case-insensitive name with a seeded one
        // (unlikely for real football clubs), the human review step this
        // verb's own workflow doc comment requires (manually verifying each
        // candidate's Wikidata QID before adding anything) is exactly the
        // safety net that catches it.
        var seededClubNames = new HashSet<string>(
            await dbContext.ClubDefinitions.AsNoTracking().Select(c => c.Name).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        // Loads every PlayerCareerStint's (ClubName, PlayerId) pair — a
        // full-table-scale read, tolerated here because this is an
        // occasional manual diagnostic job, not a hot path (contrast
        // IPlayerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync,
        // which is a hot path and narrows further to avoid loading unrelated
        // columns). The grouping/distinct-count/case-insensitive filter
        // below all happen in memory after materializing, not translated to
        // SQL, so there's no provider-specific LOWER()-translation risk to
        // worry about.
        var stints = await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Select(s => new { s.ClubName, s.PlayerId })
            .ToListAsync(cancellationToken);

        return stints
            .Where(s => !seededClubNames.Contains(s.ClubName))
            .GroupBy(s => s.ClubName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnseededClubCandidate(g.Key, g.Select(s => s.PlayerId).Distinct().Count()))
            .OrderByDescending(c => c.PlayerCount)
            .Take(top)
            .ToList();
    }
}
