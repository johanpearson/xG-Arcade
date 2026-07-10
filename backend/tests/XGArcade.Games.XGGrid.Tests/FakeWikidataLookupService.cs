using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — matches this repo's existing no-Moq/no-NSubstitute
// pattern, e.g. WikidataLookupServiceTests' FakeHttpMessageHandler). Lets
// GridGameModule tests exercise the cache-miss -> live-lookup path and the
// "unresolved QID / no match" path deterministically, without any HTTP
// machinery. Mirrors IWikidataLookupService.LookupAndPersistAsync's real
// contract: never throws, and returns empty whenever either side's
// WikidataQid is null (REQ-109) — configured matches for a pair with a null
// QID would never actually be reachable via the real service, so this fake
// enforces the same rule rather than letting a test accidentally rely on an
// impossible configuration.
//
// Also mirrors the real service's *persistence* half (the interface doc
// comment's "Returns the players persisted" — not just returned): when
// constructed with a playerStore, every configured match is actually
// upserted into Player/PlayerAttribute, same as WikidataLookupService would.
// This matters for ScoreSubmissionAsync's guess-time live-lookup fallback
// (REQ-211, Tier 0 simplified — see GridGameModule's doc comment), which
// re-checks the database after calling this and would otherwise never see
// the "live" match. playerStore is optional (defaults to null) so tests that
// only care about GenerateInstanceAsync's match-count branching, not
// persistence, aren't forced to wire one up.
public class FakeWikidataLookupService(IPlayerStoreRepository? playerStore = null) : IWikidataLookupService
{
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

    private readonly Dictionary<(string Country, string Club), List<Player>> _matches = new();

    public void SetMatches(string countryName, string clubName, IReadOnlyList<Player> players) =>
        _matches[(countryName, clubName)] = players.ToList();

    public async Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country, ClubDefinition club, CancellationToken cancellationToken = default)
    {
        if (country.WikidataQid is null || club.WikidataQid is null)
            return [];

        if (!_matches.TryGetValue((country.Name, club.Name), out var players))
            return [];

        if (playerStore is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, country.Name, club.Name, cancellationToken);
        }

        return players;
    }

    private async Task PersistAsync(Player player, string countryName, string clubName, CancellationToken cancellationToken)
    {
        var existing = player.WikidataQid is null
            ? null
            : await playerStore!.GetPlayerByWikidataQidAsync(player.WikidataQid, cancellationToken);
        var persisted = existing ?? await playerStore!.AddPlayerAsync(
            new Player { Id = player.Id, FullName = player.FullName, WikidataQid = player.WikidataQid },
            cancellationToken);

        if (!await playerStore!.HasEffectiveAttributeAsync(persisted.Id, NationalityAttributeType, countryName, cancellationToken))
            await playerStore.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = NationalityAttributeType, AttributeValue = countryName },
                cancellationToken);

        if (!await playerStore!.HasEffectiveAttributeAsync(persisted.Id, ClubAttributeType, clubName, cancellationToken))
            await playerStore.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = ClubAttributeType, AttributeValue = clubName },
                cancellationToken);
    }
}
