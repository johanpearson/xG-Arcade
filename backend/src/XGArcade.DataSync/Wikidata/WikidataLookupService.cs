using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

public class WikidataLookupService(IWikidataClient wikidataClient, IPlayerStoreRepository playerStore) : IWikidataLookupService
{
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";
    // S-031/REQ-108: PlayerAttribute.AttributeType's vocabulary spells this
    // one identically to CategoryPairingRules' "trophy" — no mapping needed,
    // unlike Country/Club.
    private const string TrophyAttributeType = "trophy";
    private const string WikidataSource = "wikidata";
    // ADR-0032: no code path in this class persists "unverified" anymore —
    // both WikidataLookupOrigin values map to VerifiedConfidence below.
    private const string VerifiedConfidence = "verified";

    // ADR-0032 (supersedes ADR-0029): both origins are now trusted as
    // ground truth and persist "verified" — the product owner decided all
    // Wikidata-sourced data should be verified by default, including
    // REQ-211's guess-time fallback, which ADR-0029 had deliberately kept
    // reviewable. WikidataLookupOrigin itself and its two callers below are
    // kept, not collapsed away — the distinction remains meaningful for
    // logging/debugging/future re-differentiation, it just no longer drives
    // a different Confidence value. Do not reintroduce a per-origin split
    // here without a new ADR superseding ADR-0032.
    private static string ConfidenceFor(WikidataLookupOrigin origin) => origin switch
    {
        WikidataLookupOrigin.Sync => VerifiedConfidence,
        WikidataLookupOrigin.GuessTimeFallback => VerifiedConfidence,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };

    public async Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country,
        ClubDefinition club,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        // REQ-109: an unresolved QID isn't an error, it just means Wikidata
        // is skipped for this value — the API-Football fallback (Tier 1)
        // doesn't need a QID at all.
        if (country.WikidataQid is null || club.WikidataQid is null)
            return [];

        // REQ-211 (2026-07-27 fix): only the guess-time fallback asks the
        // client to THROW on a timeout instead of swallowing to [] — see
        // IWikidataClient's throwOnTimeout doc comment. A generation-time
        // Sync lookup keeps the original swallow-to-[] contract completely
        // unaffected (REQ-103).
        var throwOnTimeout = origin == WikidataLookupOrigin.GuessTimeFallback;

        // REQ-114/ADR-0035: the only place this decision is made — a second
        // query path (P1532, "country for sport"), not a replacement for
        // the P27 ("country of citizenship") path every other seeded
        // country uses. Both branches persist under the exact same
        // AttributeType/AttributeValue below ("nationality"/country.Name):
        // a national-team value like "England" is just another value in
        // that vocabulary, same as "United Kingdom" already is — nothing
        // downstream of this branch (PersistMatchesAsync, grid generation,
        // guess-checking) needs to know or care which query path produced
        // the match.
        var matches = country.UsesCountryForSportProperty
            ? await wikidataClient.QueryNationalTeamClubIntersectionAsync(
                country.WikidataQid, club.WikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier)
            : await wikidataClient.QueryCountryClubIntersectionAsync(
                country.WikidataQid, club.WikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

        var persisted = await PersistMatchesAsync(
            matches, NationalityAttributeType, country.Name, ClubAttributeType, club.Name, origin, cancellationToken);

        // ADR-0042/S-079: PlayerCareerStint is populated ALONGSIDE (never
        // instead of) PlayerAttribute's "club" row above, from the same
        // response — this is the only Lookup*Async entry point this story
        // wires up (see LookupAndPersistClubClubAsync/
        // LookupAndPersistTrophyCountryAsync/LookupAndPersistTrophyClubAsync
        // below for the deliberate scope note on why they don't).
        await PersistCareerStintsAsync(matches, persisted, club.Name, cancellationToken);

        return persisted;
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA,
        ClubDefinition clubB,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (clubA.WikidataQid is null || clubB.WikidataQid is null)
            return [];

        // REQ-211 (2026-07-27 fix): see LookupAndPersistAsync's own comment
        // on throwOnTimeout — same rule for every Lookup*Async method below.
        var throwOnTimeout = origin == WikidataLookupOrigin.GuessTimeFallback;

        var matches = await wikidataClient.QueryClubClubIntersectionAsync(
            clubA.WikidataQid, clubB.WikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

        // ADR-0042/S-079: deliberately does NOT persist PlayerCareerStint —
        // this story only wires up the country/nationality x club path
        // (LookupAndPersistAsync above). Extending career-stint persistence
        // to club-club is a separate future decision, not assumed here —
        // note match.CareerStints is also structurally empty for this query
        // shape anyway (BuildClubClubIntersectionQuery's two distinctly-
        // named statement variables never bind the shared ?clubStatement
        // the qualifier OPTIONALs key off, see WikidataClient's own comment).
        return await PersistMatchesAsync(
            matches, ClubAttributeType, clubA.Name, ClubAttributeType, clubB.Name, origin, cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistTrophyCountryAsync(
        TrophyDefinition trophy,
        CountryDefinition country,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default)
    {
        if (trophy.WikidataQid is null || country.WikidataQid is null)
            return [];

        // REQ-211 (2026-07-27 fix): see LookupAndPersistAsync's own comment
        // on throwOnTimeout.
        var throwOnTimeout = origin == WikidataLookupOrigin.GuessTimeFallback;

        var matches = await wikidataClient.QueryTrophyCountryIntersectionAsync(
            trophy.WikidataQid, country.WikidataQid, throwOnTimeout, cancellationToken);

        // ADR-0042/S-079: deliberately does NOT persist PlayerCareerStint —
        // this query has no P54 clause at all (see
        // BuildTrophyCountryIntersectionQuery's own comment), so
        // match.CareerStints is always structurally empty here regardless.
        // Extending career-stint persistence beyond the country/nationality
        // x club path is a separate future decision, not assumed here.
        return await PersistMatchesAsync(
            matches, TrophyAttributeType, trophy.Name, NationalityAttributeType, country.Name, origin, cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistTrophyClubAsync(
        TrophyDefinition trophy,
        ClubDefinition club,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default)
    {
        if (trophy.WikidataQid is null || club.WikidataQid is null)
            return [];

        // REQ-211 (2026-07-27 fix): see LookupAndPersistAsync's own comment
        // on throwOnTimeout.
        var throwOnTimeout = origin == WikidataLookupOrigin.GuessTimeFallback;

        var matches = await wikidataClient.QueryTrophyClubIntersectionAsync(
            trophy.WikidataQid, club.WikidataQid, throwOnTimeout, cancellationToken);

        // ADR-0042/S-079: deliberately does NOT persist PlayerCareerStint,
        // even though this query shape DOES share the ?clubStatement
        // variable name (so match.CareerStints may be non-empty here) —
        // this story only wires up the country/nationality x club path
        // (LookupAndPersistAsync above). Extending career-stint persistence
        // to trophy-club is a separate future decision, not assumed here.
        return await PersistMatchesAsync(
            matches, TrophyAttributeType, trophy.Name, ClubAttributeType, club.Name, origin, cancellationToken);
    }

    // Batched (bug-bundle fix, 2026-07-27 — see docs/coding-guidelines.md's
    // "one SaveChangesAsync call for the whole batch" rule): the whole match
    // set is get-or-created, and every PlayerData/PlayerAttribute/
    // PlayerAlias row is added, via a small FIXED number of repository
    // round trips regardless of how many players this cell's Wikidata query
    // returned — never one round trip per player. Before this fix, the
    // per-player loop below called GetOrCreatePlayerAsync (up to 2 round
    // trips) + PersistAttributeAsync x2 (up to 4) + PersistAliasesAsync (2+)
    // per match; since intersection queries never LIMIT (implementation-
    // document.md §6a: "the result set IS the cell's complete answer key"),
    // a popular cell returning dozens of players made this the dominant cost
    // of a slow guess (REQ-211's guess-time fallback runs synchronously
    // inside the guess-submission request). Shared by LookupAndPersistAsync
    // (country + club), LookupAndPersistClubClubAsync (club + club),
    // LookupAndPersistTrophyCountryAsync, and LookupAndPersistTrophyClubAsync
    // — the only difference between callers is which attribute type/value
    // pairs the matches get persisted under.
    private async Task<IReadOnlyList<Player>> PersistMatchesAsync(
        IReadOnlyList<WikidataPlayerMatch> matches,
        string attributeTypeA, string attributeValueA,
        string attributeTypeB, string attributeValueB,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken)
    {
        if (matches.Count == 0)
            return [];

        // Upsert by WikidataQid — never insert per query (implementation-
        // document.md §6a's non-negotiable rule): the same player can be
        // returned by many different country/club intersection queries
        // across many cells and must resolve to exactly one Player row.
        // `matches` is already keyed by unique WikidataQid (WikidataClient.
        // ParseBindings groups by qid), so this request list has no
        // duplicate keys to worry about.
        var playersByQid = await playerStore.GetOrCreatePlayersByWikidataQidAsync(
            matches.Select(m => new PlayerCreationRequest(m.WikidataQid, m.FullName, m.PhotoUrl, m.Position, m.BirthYear)).ToList(),
            cancellationToken);

        // Fetched once for the whole batch rather than re-queried per
        // player — every match in this result set shares the same two
        // attribute type/value pairs (this cell's two category values).
        var playerIdsWithAttributeA = (await playerStore.GetPlayerAttributesAsync(
                attributeTypeA, attributeValueA, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();
        var playerIdsWithAttributeB = (await playerStore.GetPlayerAttributesAsync(
                attributeTypeB, attributeValueB, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();

        var existingAliasesByPlayerId = await playerStore.GetPlayerAliasesByPlayerIdsAsync(
            playersByQid.Values.Select(p => p.Id).ToList(), cancellationToken);

        var persisted = new List<Player>(matches.Count);
        var playerDataToAdd = new List<PlayerData>();
        var attributesToAdd = new List<PlayerAttribute>();
        var aliasesToAdd = new List<PlayerAlias>();
        var confidence = ConfidenceFor(origin);
        var syncedAt = DateTime.UtcNow;

        foreach (var match in matches)
        {
            var player = playersByQid[match.WikidataQid];
            persisted.Add(player);

            QueueAttribute(player.Id, attributeTypeA, attributeValueA, playerIdsWithAttributeA, confidence, syncedAt, playerDataToAdd, attributesToAdd);
            QueueAttribute(player.Id, attributeTypeB, attributeValueB, playerIdsWithAttributeB, confidence, syncedAt, playerDataToAdd, attributesToAdd);

            if (match.Aliases.Count == 0)
                continue;

            HashSet<string> existingNormalizedAliases = existingAliasesByPlayerId.TryGetValue(player.Id, out var existingAliasesForPlayer)
                ? existingAliasesForPlayer.Select(a => a.NormalizedAlias).ToHashSet()
                : [];

            foreach (var alias in match.Aliases)
            {
                var normalized = PlayerNameNormalizer.Normalize(alias);
                if (existingNormalizedAliases.Add(normalized))
                    aliasesToAdd.Add(new PlayerAlias { PlayerId = player.Id, Alias = alias, NormalizedAlias = normalized });
            }
        }

        // Three more fixed-count repository calls (never one per player) —
        // GetOrCreatePlayersByWikidataQidAsync above already contributed the
        // batch's own SaveChangesAsync for the Player rows themselves.
        await playerStore.AddPlayerDataBatchAsync(playerDataToAdd, cancellationToken);
        await playerStore.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);
        await playerStore.AddPlayerAliasesBatchAsync(aliasesToAdd, cancellationToken);

        return persisted;
    }

    // PlayerData is a raw, per-source append log (its own SyncedAt
    // timestamps each sync) — always recorded. PlayerAttribute is the
    // effective, denormalized view with a composite key on (PlayerId,
    // AttributeType, AttributeValue), so it must be guarded against a
    // duplicate insert across repeated lookups — same dedup rule as before
    // this method was batched, just queuing into the batch lists
    // PersistMatchesAsync flushes afterwards instead of writing immediately.
    private static void QueueAttribute(
        Guid playerId, string attributeType, string attributeValue,
        HashSet<Guid> playerIdsAlreadyHavingThisAttribute,
        string confidence, DateTime syncedAt,
        List<PlayerData> playerDataToAdd, List<PlayerAttribute> attributesToAdd)
    {
        playerDataToAdd.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = attributeType,
            Value = attributeValue,
            Source = WikidataSource,
            Confidence = confidence,
            SyncedAt = syncedAt,
        });

        if (playerIdsAlreadyHavingThisAttribute.Add(playerId))
            attributesToAdd.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = attributeType, AttributeValue = attributeValue });
    }

    // ADR-0042/S-079: only called from LookupAndPersistAsync (see that
    // method's own comment) — matches and persistedPlayers are the same
    // length and share the same index-for-index ordering, since
    // PersistMatchesAsync's loop iterates `matches` once, in order, adding
    // exactly one Player per match to its returned list.
    //
    // Batched the same way PersistMatchesAsync above is (bug-bundle fix,
    // 2026-07-27): one bulk GetCareerStintsByPlayerIdsAsync fetch (for every
    // player this match set assigned at least one CareerStints qualifier
    // to) instead of one GetCareerStintsAsync round trip per player, and one
    // AddCareerStintsBatchAsync call instead of one per player.
    private async Task PersistCareerStintsAsync(
        IReadOnlyList<WikidataPlayerMatch> matches,
        IReadOnlyList<Player> persistedPlayers,
        string clubName,
        CancellationToken cancellationToken)
    {
        var playerIdsWithStints = new List<Guid>();
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].CareerStints.Count > 0)
                playerIdsWithStints.Add(persistedPlayers[i].Id);
        }

        if (playerIdsWithStints.Count == 0)
            return;

        var existingStintsByPlayerId = await playerStore.GetCareerStintsByPlayerIdsAsync(playerIdsWithStints, cancellationToken);

        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.CareerStints.Count == 0)
                continue;

            var playerId = persistedPlayers[i].Id;

            // Idempotency: re-running the same query must not create
            // duplicate stint rows — skip a tuple that already exists for
            // this player (same ClubName/StartYear/EndYear/AppearanceCount),
            // same "fetch once, HashSet.Add as the dedup+select gate"
            // pattern as before this method was batched.
            HashSet<(string ClubName, int StartYear, int? EndYear, int? AppearanceCount)> seenTuples =
                existingStintsByPlayerId.TryGetValue(playerId, out var existingStints)
                    ? existingStints.Select(s => (s.ClubName, s.StartYear, s.EndYear, s.AppearanceCount)).ToHashSet()
                    : [];

            var newStints = match.CareerStints
                .Where(q => seenTuples.Add((clubName, q.StartYear, q.EndYear, q.AppearanceCount)))
                .Select(q => new PlayerCareerStint
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    ClubName = clubName,
                    StartYear = q.StartYear,
                    EndYear = q.EndYear,
                    AppearanceCount = q.AppearanceCount,
                    // Resolved by IPlayerStoreRepository.AddCareerStintsBatchAsync
                    // across the player's full stint set — this placeholder
                    // is always overwritten before SaveChangesAsync.
                    SequenceOrder = 0,
                })
                .ToList();

            if (newStints.Count > 0)
                newStintsByPlayerId[playerId] = newStints;
        }

        await playerStore.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);
    }
}
