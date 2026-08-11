using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// ADR-0056: xG Path had no fame/recognizability signal at all before this —
// REQ-1201's eligibility check only asked "does this player have a
// well-defined, orderable career path," never "would a casual player
// recognize this name." Real feedback from playing the game: a structurally
// eligible but obscure target (a journeyman with a long, well-documented but
// unglamorous career) makes a puzzle un-fun to solve, since none of REQ-1203's
// clues (clubs, position, nationality, birth year) reliably narrow down to
// someone the player has ever heard of. Wikipedia sitelink count (how many
// language editions have an article on this person) is used as the
// familiarity proxy — see IWikidataClient.QuerySitelinkCountsByQidsAsync's
// own doc comment for why this signal specifically was chosen over
// alternatives (total career appearances, trophies won) and ADR-0056 for the
// full trade-off writeup.
//
// Same "narrow, purpose-built service around IWikidataClient, injected into
// XGPathGameModule rather than the raw client" shape as
// PlayerCareerStintRefreshService (ADR-0054) — see that class's own doc
// comment for "why sequential, not concurrent DbContext use" and "why a
// malformed WikidataQid is filtered out and logged before the batch is sent"
// reasoning, which applies identically here and isn't repeated in full below.
// S-106 (pure refactor): IPlayerStoreRepository's own GetPlayersByIdsAsync
// moved to IPlayerRepository — this class's only player-store call, so it
// takes the narrower interface directly rather than IPlayerStoreRepository.
public class PlayerFamiliarityService(
    IWikidataClient wikidataClient,
    IPlayerRepository playerRepository,
    ILogger<PlayerFamiliarityService> logger) : IPlayerFamiliarityService
{
    // ADR-0056: starting threshold, deliberately conservative rather than
    // tuned against real usage data (none exists yet) — a top-tier star
    // easily clears 50-100+ language editions, a solid, well-known
    // top-flight international is comfortably above this, and a genuinely
    // obscure journeyman (the reported "Austrian guy" complaint) typically
    // sits in the single digits. Flagged as a judgment call for
    // architecture-reviewer/product to revisit once real puzzles have been
    // played against it — see ADR-0056's own Follow-up section.
    public const int MinSitelinkCount = 15;

    // Same conservative batch size as PlayerPhotoBackfillService/
    // PlayerPositionBirthYearBackfillService.BatchSize — safely inside the
    // bounded-query budget implementation-document.md §6a establishes.
    public const int BatchSize = 200;

    public async Task<IReadOnlySet<Guid>> FilterFamiliarAsync(
        IReadOnlyList<Guid> candidatePlayerIds, CancellationToken cancellationToken = default)
    {
        if (candidatePlayerIds.Count == 0)
            return new HashSet<Guid>();

        var players = await playerRepository.GetPlayersByIdsAsync(candidatePlayerIds, cancellationToken);

        // Same "one bad/missing row costs only that row" discipline as
        // PlayerCareerStintRefreshService.RefreshCareerStintsAsync — a
        // candidate with no resolvable WikidataQid simply can't be
        // fame-checked, so it falls out of qidToPlayerId and is excluded
        // below rather than silently passing the filter unverified (see this
        // method's own final paragraph for why "can't verify" means
        // "excluded," not "assumed familiar").
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
        {
            // Nobody in this pool can be fame-checked at all — a systemic
            // data gap, not evidence any specific candidate is unfamiliar.
            // Fail open (REQ-103's "never block round generation on a
            // Wikidata failure" reasoning, applied to a data gap rather than
            // a query failure): skip the filter entirely rather than
            // rejecting the whole pool.
            return candidatePlayerIds.ToHashSet();
        }

        var sitelinkCountsByQid = new Dictionary<string, int>();
        foreach (var batch in qidToPlayerId.Keys.Chunk(BatchSize))
        {
            try
            {
                var batchResult = await wikidataClient.QuerySitelinkCountsByQidsAsync(batch, cancellationToken);
                foreach (var (qid, count) in batchResult)
                    sitelinkCountsByQid[qid] = count;
            }
            catch (WikidataQueryException ex)
            {
                // Fail open for the WHOLE pool, not just this batch — a
                // partially-applied familiarity filter (some candidates
                // checked, some not, for reasons that have nothing to do
                // with actual fame) would bias target selection in an
                // unprincipled way. Same "never block xG Path round
                // generation on a Wikidata failure" reasoning
                // PlayerCareerStintRefreshService.RefreshCareerStintsAsync's
                // own catch block already establishes — this round's target
                // pool simply skips the familiarity filter this once, and
                // the next generation tries again.
                logger.LogWarning(ex,
                    "xg-path familiarity filter: sitelink batch of {BatchSize} QID(s) failed; " +
                    "skipping the familiarity filter for this round generation rather than blocking it.",
                    batch.Length);
                return candidatePlayerIds.ToHashSet();
            }
        }

        // A candidate is judged familiar only when its sitelink count both
        // resolved AND met the threshold — a candidate whose QID couldn't be
        // fame-checked (missing/invalid WikidataQid, filtered out above) or
        // whose sitelink count never resolved (absent from
        // sitelinkCountsByQid, per QuerySitelinkCountsByQidsAsync's own
        // "absent means unknown, never confirmed 0" contract) is excluded,
        // not given the benefit of the doubt — the whole point of this
        // filter is to positively confirm familiarity, not merely fail to
        // disprove it.
        return qidToPlayerId
            .Where(kv => sitelinkCountsByQid.TryGetValue(kv.Key, out var count) && count >= MinSitelinkCount)
            .Select(kv => kv.Value)
            .ToHashSet();
    }
}
