using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Seeding;

// One-time backfill for PlayerAlias.NormalizedAlias (diacritic-normalization
// bug fix, 2026-08-02): rows persisted before PlayerNameNormalizer gained its
// non-decomposable-Latin-letter mapping (Ø/Æ/Œ/Đ/Ł/ß/Þ — see that class's own
// doc comment) have a stale NormalizedAlias computed under the OLD, broken
// normalization — e.g. an "Ødegaard" alias was stored as "ødegaard" instead
// of "odegaard" — which permanently fails IPlayerStoreRepository
// .GetPlayersByNormalizedAliasAsync (the alias fallback both
// GridGameModule.FindMatchAsync and XGPathGameModule.ScoreSubmissionAsync
// use) for any alias containing one of those letters, with no error or log
// line — the same silent-failure shape PlayerNormalizedFullNameBackfiller's
// own doc comment describes for Player.FullName.
//
// Deliberately NOT PlayerNormalizedFullNameBackfiller's "reassign the
// property, let the setter recompute" shape: PlayerAlias has no such setter
// (Alias/NormalizedAlias are plain auto-properties, unlike Player.FullName)
// — AND, the real reason this needs its own backfiller rather than being
// folded into that one, NormalizedAlias is HALF of PlayerAlias's composite
// primary key ((PlayerId, NormalizedAlias), see XGArcadeDbContext.HasKey).
// Mutating a tracked entity's own primary key property is a distinct,
// riskier operation than mutating a plain column (Player.NormalizedFullName
// is not part of Player's key), so this backfiller sidesteps it entirely: it
// computes each player's DESIRED final set of (NormalizedAlias, Alias) pairs
// under the current normalizer, diffs that against what's actually stored,
// and only ever removes/adds whole rows — never assigns a new value to an
// existing row's NormalizedAlias property in place.
//
// Idempotent and safe to re-run, same contract as every other backfiller in
// this file: once every row's stored NormalizedAlias already matches what
// PlayerNameNormalizer.Normalize(Alias) recomputes, the desired/current sets
// are identical for every player and nothing is queued for
// SaveChangesAsync. Collision-safe by construction: `desired` is built as a
// dictionary keyed by the RECOMPUTED normalized value, so if two of a
// player's stored aliases happen to converge on the same value once
// re-normalized (e.g. a literal "Odegaard" alias already existed alongside
// the stale "ødegaard" row for "Ødegaard"), only one row is kept — the
// composite-key uniqueness constraint that would otherwise reject a
// duplicate insert can never be reached.
public static class PlayerAliasNormalizedAliasBackfiller
{
    public static async Task BackfillAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var aliases = await dbContext.PlayerAliases.ToListAsync(cancellationToken);
        if (aliases.Count == 0)
            return;

        foreach (var group in aliases.GroupBy(a => a.PlayerId))
        {
            // Desired final state for this player: one row per DISTINCT
            // recomputed normalized value, keeping the first Alias text seen
            // for it — same "first-seen wins" defensive shape used
            // throughout WikidataClient's own binding-parsing code.
            var desired = new Dictionary<string, string>(); // normalized -> alias text
            foreach (var alias in group)
            {
                var recomputed = PlayerNameNormalizer.Normalize(alias.Alias);
                if (!desired.ContainsKey(recomputed))
                    desired[recomputed] = alias.Alias;
            }

            // Safe: (PlayerId, NormalizedAlias) is this table's actual
            // composite primary key, so a player can never have two stored
            // rows sharing the same NormalizedAlias — ToDictionary here can
            // never throw on a duplicate key.
            var current = group.ToDictionary(a => a.NormalizedAlias, a => a);

            foreach (var (normalizedAlias, row) in current)
            {
                if (!desired.ContainsKey(normalizedAlias))
                    dbContext.PlayerAliases.Remove(row);
            }

            foreach (var (normalizedAlias, aliasText) in desired)
            {
                if (!current.ContainsKey(normalizedAlias))
                {
                    dbContext.PlayerAliases.Add(new PlayerAlias
                    {
                        PlayerId = group.Key,
                        Alias = aliasText,
                        NormalizedAlias = normalizedAlias,
                    });
                }
            }
        }

        // One SaveChangesAsync call for the whole backfill — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync/ExecuteDeleteAsync (the InMemory test provider
        // can't translate them).
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
