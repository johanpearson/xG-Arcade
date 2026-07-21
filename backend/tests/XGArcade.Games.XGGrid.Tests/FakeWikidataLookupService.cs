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
    private const string TrophyAttributeType = "trophy";

    private readonly Dictionary<(string Country, string Club), List<Player>> _matches = new();
    private readonly Dictionary<(string Country, string Club), int> _callCounts = new();
    // ADR-0029: the most recent WikidataLookupOrigin each pair was called
    // with — lets a test assert GetMatchCountAsync (generation-time) and
    // RefreshCellFromLiveLookupAsync (REQ-211 guess-time fallback) each pass
    // the origin they're supposed to, without any real persistence to
    // inspect (this fake doesn't write PlayerData/Confidence itself).
    private readonly Dictionary<(string Country, string Club), WikidataLookupOrigin> _lastOrigin = new();
    // REQ-114/ADR-0035: the most recent CountryDefinition.UsesCountryForSportProperty
    // each Country x Club pair's LookupAndPersistAsync call was made with —
    // lets a test assert GridGameModule threads the flag through
    // CategoryCandidate/LookupLiveMatchesAsync correctly, without any real
    // WikidataClient dispatch to inspect (this fake doesn't call one).
    private readonly Dictionary<(string Country, string Club), bool> _lastUsesCountryForSportProperty = new();
    // S-030: a second, independent pair of dictionaries for Club x Club —
    // kept separate from the Country x Club ones above (rather than sharing
    // one dictionary keyed loosely by two strings) so a test can't
    // accidentally cross-contaminate a Country x Club expectation with a
    // Club x Club one that happens to share a name.
    private readonly Dictionary<(string ClubA, string ClubB), List<Player>> _clubClubMatches = new();
    private readonly Dictionary<(string ClubA, string ClubB), int> _clubClubCallCounts = new();
    private readonly Dictionary<(string ClubA, string ClubB), WikidataLookupOrigin> _clubClubLastOrigin = new();
    // S-031: Trophy x Country and Trophy x Club, kept separate from the
    // dictionaries above for the same "no accidental cross-contamination"
    // reason as the Club x Club ones.
    private readonly Dictionary<(string Trophy, string Country), List<Player>> _trophyCountryMatches = new();
    private readonly Dictionary<(string Trophy, string Country), int> _trophyCountryCallCounts = new();
    private readonly Dictionary<(string Trophy, string Country), WikidataLookupOrigin> _trophyCountryLastOrigin = new();
    private readonly Dictionary<(string Trophy, string Club), List<Player>> _trophyClubMatches = new();
    private readonly Dictionary<(string Trophy, string Club), int> _trophyClubCallCounts = new();
    private readonly Dictionary<(string Trophy, string Club), WikidataLookupOrigin> _trophyClubLastOrigin = new();

    public void SetMatches(string countryName, string clubName, IReadOnlyList<Player> players) =>
        _matches[(countryName, clubName)] = players.ToList();

    public void SetClubClubMatches(string clubAName, string clubBName, IReadOnlyList<Player> players) =>
        _clubClubMatches[(clubAName, clubBName)] = players.ToList();

    public void SetTrophyCountryMatches(string trophyName, string countryName, IReadOnlyList<Player> players) =>
        _trophyCountryMatches[(trophyName, countryName)] = players.ToList();

    public void SetTrophyClubMatches(string trophyName, string clubName, IReadOnlyList<Player> players) =>
        _trophyClubMatches[(trophyName, clubName)] = players.ToList();

    // REQ-211's fallback must call this at most once per guess (bounded by
    // REQ-210's attempt cap, ADR-0018) — exposed so a test can assert the
    // fallback doesn't loop/recurse even when the re-run still finds nothing
    // that answers the guess.
    public int GetCallCount(string countryName, string clubName) =>
        _callCounts.TryGetValue((countryName, clubName), out var count) ? count : 0;

    public int GetClubClubCallCount(string clubAName, string clubBName) =>
        _clubClubCallCounts.TryGetValue((clubAName, clubBName), out var count) ? count : 0;

    public WikidataLookupOrigin? GetLastOrigin(string countryName, string clubName) =>
        _lastOrigin.TryGetValue((countryName, clubName), out var origin) ? origin : null;

    public bool? GetLastUsesCountryForSportProperty(string countryName, string clubName) =>
        _lastUsesCountryForSportProperty.TryGetValue((countryName, clubName), out var flag) ? flag : null;

    public WikidataLookupOrigin? GetClubClubLastOrigin(string clubAName, string clubBName) =>
        _clubClubLastOrigin.TryGetValue((clubAName, clubBName), out var origin) ? origin : null;

    public int GetTrophyCountryCallCount(string trophyName, string countryName) =>
        _trophyCountryCallCounts.TryGetValue((trophyName, countryName), out var count) ? count : 0;

    public int GetTrophyClubCallCount(string trophyName, string clubName) =>
        _trophyClubCallCounts.TryGetValue((trophyName, clubName), out var count) ? count : 0;

    public WikidataLookupOrigin? GetTrophyCountryLastOrigin(string trophyName, string countryName) =>
        _trophyCountryLastOrigin.TryGetValue((trophyName, countryName), out var origin) ? origin : null;

    public WikidataLookupOrigin? GetTrophyClubLastOrigin(string trophyName, string clubName) =>
        _trophyClubLastOrigin.TryGetValue((trophyName, clubName), out var origin) ? origin : null;

    public async Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country, ClubDefinition club, WikidataLookupOrigin origin, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _callCounts[(country.Name, club.Name)] = GetCallCount(country.Name, club.Name) + 1;
        _lastOrigin[(country.Name, club.Name)] = origin;
        _lastUsesCountryForSportProperty[(country.Name, club.Name)] = country.UsesCountryForSportProperty;

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
        ClubDefinition clubA, ClubDefinition clubB, WikidataLookupOrigin origin, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _clubClubCallCounts[(clubA.Name, clubB.Name)] = GetClubClubCallCount(clubA.Name, clubB.Name) + 1;
        _clubClubLastOrigin[(clubA.Name, clubB.Name)] = origin;

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

    public async Task<IReadOnlyList<Player>> LookupAndPersistTrophyCountryAsync(
        TrophyDefinition trophy, CountryDefinition country, WikidataLookupOrigin origin, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _trophyCountryCallCounts[(trophy.Name, country.Name)] = GetTrophyCountryCallCount(trophy.Name, country.Name) + 1;
        _trophyCountryLastOrigin[(trophy.Name, country.Name)] = origin;

        if (trophy.WikidataQid is null || country.WikidataQid is null)
            return [];

        if (!_trophyCountryMatches.TryGetValue((trophy.Name, country.Name), out var players))
            return [];

        if (playerStore is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, TrophyAttributeType, trophy.Name, NationalityAttributeType, country.Name, cancellationToken);
        }

        return players;
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistTrophyClubAsync(
        TrophyDefinition trophy, ClubDefinition club, WikidataLookupOrigin origin, CancellationToken cancellationToken = default)
    {
        onCalled?.Invoke();
        _trophyClubCallCounts[(trophy.Name, club.Name)] = GetTrophyClubCallCount(trophy.Name, club.Name) + 1;
        _trophyClubLastOrigin[(trophy.Name, club.Name)] = origin;

        if (trophy.WikidataQid is null || club.WikidataQid is null)
            return [];

        if (!_trophyClubMatches.TryGetValue((trophy.Name, club.Name), out var players))
            return [];

        if (playerStore is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, TrophyAttributeType, trophy.Name, ClubAttributeType, club.Name, cancellationToken);
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
