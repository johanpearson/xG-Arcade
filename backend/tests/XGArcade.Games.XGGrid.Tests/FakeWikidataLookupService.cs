using XGArcade.Data.Entities;
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
public class FakeWikidataLookupService : IWikidataLookupService
{
    private readonly Dictionary<(string Country, string Club), List<Player>> _matches = new();

    public void SetMatches(string countryName, string clubName, IReadOnlyList<Player> players) =>
        _matches[(countryName, clubName)] = players.ToList();

    public Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country, ClubDefinition club, CancellationToken cancellationToken = default)
    {
        if (country.WikidataQid is null || club.WikidataQid is null)
            return Task.FromResult<IReadOnlyList<Player>>([]);

        if (_matches.TryGetValue((country.Name, club.Name), out var players))
            return Task.FromResult<IReadOnlyList<Player>>(players);

        return Task.FromResult<IReadOnlyList<Player>>([]);
    }
}
