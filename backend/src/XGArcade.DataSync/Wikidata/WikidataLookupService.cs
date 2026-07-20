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
        CancellationToken cancellationToken = default)
    {
        // REQ-109: an unresolved QID isn't an error, it just means Wikidata
        // is skipped for this value — the API-Football fallback (Tier 1)
        // doesn't need a QID at all.
        if (country.WikidataQid is null || club.WikidataQid is null)
            return [];

        var matches = await wikidataClient.QueryCountryClubIntersectionAsync(
            country.WikidataQid, club.WikidataQid, cancellationToken);

        return await PersistMatchesAsync(
            matches, NationalityAttributeType, country.Name, ClubAttributeType, club.Name, origin, cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA,
        ClubDefinition clubB,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default)
    {
        if (clubA.WikidataQid is null || clubB.WikidataQid is null)
            return [];

        var matches = await wikidataClient.QueryClubClubIntersectionAsync(
            clubA.WikidataQid, clubB.WikidataQid, cancellationToken);

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

        var matches = await wikidataClient.QueryTrophyCountryIntersectionAsync(
            trophy.WikidataQid, country.WikidataQid, cancellationToken);

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

        var matches = await wikidataClient.QueryTrophyClubIntersectionAsync(
            trophy.WikidataQid, club.WikidataQid, cancellationToken);

        return await PersistMatchesAsync(
            matches, TrophyAttributeType, trophy.Name, ClubAttributeType, club.Name, origin, cancellationToken);
    }

    // Fetched once for the whole batch rather than re-queried per player —
    // every match in this result set shares the same two attribute
    // type/value pairs (this cell's two category values). Shared by both
    // LookupAndPersistAsync (country + club) and LookupAndPersistClubClubAsync
    // (club + club) — the only difference between the two callers is which
    // attribute type/value pairs the matches get persisted under.
    private async Task<IReadOnlyList<Player>> PersistMatchesAsync(
        IReadOnlyList<WikidataPlayerMatch> matches,
        string attributeTypeA, string attributeValueA,
        string attributeTypeB, string attributeValueB,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken)
    {
        if (matches.Count == 0)
            return [];

        var playerIdsWithAttributeA = (await playerStore.GetPlayerAttributesAsync(
                attributeTypeA, attributeValueA, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();
        var playerIdsWithAttributeB = (await playerStore.GetPlayerAttributesAsync(
                attributeTypeB, attributeValueB, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();

        var persisted = new List<Player>(matches.Count);

        foreach (var match in matches)
        {
            var player = await GetOrCreatePlayerAsync(match, cancellationToken);

            await PersistAttributeAsync(player.Id, attributeTypeA, attributeValueA, playerIdsWithAttributeA, origin, cancellationToken);
            await PersistAttributeAsync(player.Id, attributeTypeB, attributeValueB, playerIdsWithAttributeB, origin, cancellationToken);
            await PersistAliasesAsync(player.Id, match.Aliases, cancellationToken);

            persisted.Add(player);
        }

        return persisted;
    }

    // Upsert by WikidataQid — never insert per query (implementation-
    // document.md §6a's non-negotiable rule): the same player can be
    // returned by many different country/club intersection queries across
    // many cells and must resolve to exactly one Player row.
    private async Task<Player> GetOrCreatePlayerAsync(WikidataPlayerMatch match, CancellationToken cancellationToken)
    {
        var existing = await playerStore.GetPlayerByWikidataQidAsync(match.WikidataQid, cancellationToken);
        if (existing is not null)
            return existing;

        return await playerStore.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = match.FullName, WikidataQid = match.WikidataQid, PhotoUrl = match.PhotoUrl },
            cancellationToken);
    }

    private async Task PersistAttributeAsync(
        Guid playerId,
        string attributeType,
        string attributeValue,
        HashSet<Guid> playerIdsAlreadyHavingThisAttribute,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken)
    {
        // PlayerData is a raw, per-source append log (its own SyncedAt
        // timestamps each sync) — always recorded. PlayerAttribute is the
        // effective, denormalized view with a composite key on
        // (PlayerId, AttributeType, AttributeValue), so it must be guarded
        // against a duplicate insert across repeated lookups.
        await playerStore.AddPlayerDataAsync(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = attributeType,
            Value = attributeValue,
            Source = WikidataSource,
            Confidence = ConfidenceFor(origin),
            SyncedAt = DateTime.UtcNow,
        }, cancellationToken);

        if (!playerIdsAlreadyHavingThisAttribute.Add(playerId))
            return;

        await playerStore.AddPlayerAttributeAsync(
            new PlayerAttribute { PlayerId = playerId, AttributeType = attributeType, AttributeValue = attributeValue },
            cancellationToken);
    }

    private async Task PersistAliasesAsync(Guid playerId, IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        if (aliases.Count == 0)
            return;

        var existingNormalizedAliases = (await playerStore.GetPlayerAliasesAsync(playerId, cancellationToken))
            .Select(a => a.NormalizedAlias)
            .ToHashSet();

        foreach (var alias in aliases)
        {
            var normalized = PlayerNameNormalizer.Normalize(alias);
            if (!existingNormalizedAliases.Add(normalized))
                continue;

            await playerStore.AddPlayerAliasAsync(
                new PlayerAlias { PlayerId = playerId, Alias = alias, NormalizedAlias = normalized },
                cancellationToken);
        }
    }
}
