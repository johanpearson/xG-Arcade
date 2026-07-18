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
//
// ADR-0023: onCalled fires at the start of every live lookup, before this
// fake's own configured-match logic runs — lets a test simulate a live
// call's real-world latency (e.g. advancing a ManualTimeProvider) without
// any actual waiting, so PickHeadersAsync's MaxDuration deadline-abort
// branch can be exercised deterministically.
public class FakeWikidataLookupService(IPlayerStoreRepository? playerStore = null, Action? onCalled = null) : IWikidataLookupService
{
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

    private readonly Dictionary<(string Country, string Club), List<Player>> _matches = new();
    private readonly Dictionary<(string Country, string Club), int> _callCounts = new();
    // S-030: a second, independent pair of dictionaries for Club x Club —
    // kept separate from the Country x Club ones above (rather than sharing
    // one dictionary keyed loosely by two strings) so a test can't
    // accidentally cross-contaminate a Country x Club expectation with a
    // Club x Club one that happens to share a name.
    private readonly Dictionary<(string ClubA, string ClubB), List<Player>> _clubClubMatches = new();
    private readonly Dictionary<(string ClubA, string ClubB), int> _clubClubCallCounts = new();

    public void SetMatches(string countryName, string clubName, IReadOnlyList<Player> players) =>
        _matches[(countryName, clubName)] = players.ToList();

    public void SetClubClubMatches(string clubAName, string clubBName, IReadOnlyList<Player> players) =>
        _clubClubMatches[(clubAName, clubBName)] = players.ToList();

    // REQ-211's fallback must call this at most once per guess (bounded by
    // REQ-210's attempt cap, ADR-0018) — exposed so a test can assert the
    // fallback doesn't loop/recurse even when the re-run still finds nothing
    // that answers the guess.
    public int GetCallCount(string countryName, string clubName) =>
        _callCounts.TryGetValue((countryName, clubName), out var count) ? count : 0;

    public int GetClubClubCallCount(string clubAName, string clubBName) =>
        _clubClubCallCounts.TryGetValue((clubAName, clubBName), out var count) ? count : 0;

    public async Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country, ClubDefinition club, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _callCounts[(country.Name, club.Name)] = GetCallCount(country.Name, club.Name) + 1;

        if (country.WikidataQid is null || club.WikidataQid is null)
            return [];

        if (!_matches.TryGetValue((country.Name, club.Name), out var players))
            return [];

        if (playerStore is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
        }

        return players;
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA, ClubDefinition clubB, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _clubClubCallCounts[(clubA.Name, clubB.Name)] = GetClubClubCallCount(clubA.Name, clubB.Name) + 1;

        if (clubA.WikidataQid is null || clubB.WikidataQid is null)
            return [];

        if (!_clubClubMatches.TryGetValue((clubA.Name, clubB.Name), out var players))
            return [];

        if (playerStore is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, ClubAttributeType, clubA.Name, ClubAttributeType, clubB.Name, cancellationToken);
        }

        return players;
    }

    private async Task PersistAsync(
        Player player,
        string attributeTypeA, string attributeValueA,
        string attributeTypeB, string attributeValueB,
        CancellationToken cancellationToken)
    {
        var existing = player.WikidataQid is null
            ? null
            : await playerStore!.GetPlayerByWikidataQidAsync(player.WikidataQid, cancellationToken);
        var persisted = existing ?? await playerStore!.AddPlayerAsync(
            new Player { Id = player.Id, FullName = player.FullName, WikidataQid = player.WikidataQid, PhotoUrl = player.PhotoUrl },
            cancellationToken);

        if (!await playerStore!.HasEffectiveAttributeAsync(persisted.Id, attributeTypeA, attributeValueA, cancellationToken))
            await playerStore.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = attributeTypeA, AttributeValue = attributeValueA },
                cancellationToken);

        if (!await playerStore!.HasEffectiveAttributeAsync(persisted.Id, attributeTypeB, attributeValueB, cancellationToken))
            await playerStore.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = attributeTypeB, AttributeValue = attributeValueB },
                cancellationToken);
    }
}
