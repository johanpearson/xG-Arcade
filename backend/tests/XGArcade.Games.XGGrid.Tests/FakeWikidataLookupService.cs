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
// constructed with a playerOverrideRepository, every configured match is
// actually upserted into Player/PlayerAttribute, same as
// WikidataLookupService would. This matters for ScoreSubmissionAsync's
// guess-time live-lookup fallback (REQ-211, Tier 0 simplified — see
// GridGameModule's doc comment), which re-checks the database after calling
// this and would otherwise never see the "live" match.
// playerOverrideRepository is optional (defaults to null) so tests that
// only care about GenerateInstanceAsync's match-count branching, not
// persistence, aren't forced to wire one up.
//
// ADR-0023: onCalled fires at the start of every live lookup, before this
// fake's own configured-match logic runs — lets a test simulate a live
// call's real-world latency (e.g. advancing a ManualTimeProvider) without
// any actual waiting, so PickHeadersAsync's MaxDuration deadline-abort
// branch can be exercised deterministically.
//
// S-106/S-107 (pure refactor): playerRepository/playerAttributeRepository
// carry the methods this fake's own PersistAsync needs
// (GetPlayerByWikidataQidAsync/AddPlayerAsync, AddPlayerAttributeAsync) —
// playerOverrideRepository is kept for HasEffectiveAttributeAsync (see
// ADR-0067 for the full split of the original, now-deleted
// IPlayerStoreRepository). All three are still optional together
// (defaulting to null) for tests that only care about match-count
// branching, not persistence; a test that wants persistence must supply
// all three, same as the real WikidataLookupService needing every sibling
// repository together.
public class FakeWikidataLookupService(
    IPlayerOverrideRepository? playerOverrideRepository = null,
    IPlayerRepository? playerRepository = null,
    IPlayerAttributeRepository? playerAttributeRepository = null,
    Action? onCalled = null) : IWikidataLookupService
{
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";
    private const string TrophyAttributeType = "trophy";

    private readonly Dictionary<(string Country, string Club), List<Player>> _matches = new();
    private readonly Dictionary<(string Country, string Club), int> _callCounts = new();
    // REQ-211 (2026-07-27 fix): simulates the real WikidataLookupService's
    // guess-time-fallback-only "throw instead of swallow on timeout"
    // contract (WikidataQueryException) — lets GridGameModuleTests exercise
    // its catch-and-translate-to-LiveLookupUnavailableException behavior
    // without any real HTTP/timeout machinery. Only wired up for the
    // Country x Club pair (the only pairing this bug bundle's tests need to
    // cover); extend to the other three dictionaries below if a future test
    // needs it for Club x Club/Trophy pairings.
    private readonly HashSet<(string Country, string Club)> _timeoutFailures = new();
    // REQ-110 (2026-07-28): simulates WikidataClient's throwOnTimeout=false
    // technical-failure path (WDQS timeout, HTTP error, or JSON parse
    // error) — distinct from _timeoutFailures above, which simulates the
    // throwOnTimeout=true (guess-time fallback) path that throws instead.
    // A configured pair returns an empty match list (same shape as a
    // genuine zero-match success) but invokes onTechnicalFailure, mirroring
    // the real WikidataLookupService/WikidataClient contract this fake
    // stands in for.
    private readonly HashSet<(string Country, string Club)> _technicalFailures = new();
    private readonly HashSet<(string ClubA, string ClubB)> _clubClubTechnicalFailures = new();
    // REQ-110 (2026-07-28 "cache-warming-specific timeout + same-run retry"
    // extension): a COUNTDOWN, distinct from the "always fails" HashSets
    // above — lets a test script "the next N calls for this pair fail, then
    // the call after that succeeds" (e.g. PlayerCacheWarmingServiceTests'
    // same-run-retry coverage), which _technicalFailures/_clubClubTechnicalFailures
    // alone can't express (they never stop failing once added).
    private readonly Dictionary<(string Country, string Club), int> _remainingTechnicalFailureAttempts = new();
    private readonly Dictionary<(string ClubA, string ClubB), int> _clubClubRemainingTechnicalFailureAttempts = new();
    // REQ-110 (2026-07-28): the most recent WikidataQueryTimeoutTier each
    // pair's LookupAndPersistAsync/LookupAndPersistClubClubAsync call was
    // made with — lets a test assert PlayerCacheWarmingService passes
    // WikidataQueryTimeoutTier.CacheWarming while REQ-103/REQ-211's own
    // callers (GridGameModule) keep passing (or omitting, which defaults to)
    // WikidataQueryTimeoutTier.Default, mirroring _lastOrigin's own pattern.
    private readonly Dictionary<(string Country, string Club), WikidataQueryTimeoutTier> _lastTimeoutTier = new();
    private readonly Dictionary<(string ClubA, string ClubB), WikidataQueryTimeoutTier> _clubClubLastTimeoutTier = new();
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
    // ADR-0061: the most recent TrophyDefinition.IsTeamTrophy/
    // CountryDefinition.UsesCountryForSportProperty each Trophy x Country
    // pair's LookupAndPersistTrophyCountryAsync call was made with — lets a
    // test assert GridGameModule threads both flags through
    // CategoryCandidate/LookupLiveMatchesAsync correctly, mirroring
    // _lastUsesCountryForSportProperty's own precedent for Country x Club.
    private readonly Dictionary<(string Trophy, string Country), bool> _trophyCountryLastIsTeamTrophy = new();
    private readonly Dictionary<(string Trophy, string Country), bool> _trophyCountryLastUsesCountryForSportProperty = new();
    // ADR-0061: the Trophy x Club counterpart of _trophyCountryLastIsTeamTrophy
    // above.
    private readonly Dictionary<(string Trophy, string Club), bool> _trophyClubLastIsTeamTrophy = new();

    public void SetMatches(string countryName, string clubName, IReadOnlyList<Player> players) =>
        _matches[(countryName, clubName)] = players.ToList();

    // REQ-211 (2026-07-27 fix): the next LookupAndPersistAsync call for this
    // pair throws WikidataQueryException instead of returning a result —
    // simulates a guess-time-fallback timeout.
    public void FailWithTimeout(string countryName, string clubName) =>
        _timeoutFailures.Add((countryName, clubName));

    // REQ-110: the next LookupAndPersistAsync call for this pair invokes
    // onTechnicalFailure and returns an empty match list, instead of
    // whatever SetMatches configured — see _technicalFailures' own comment.
    public void FailWithTechnicalFailure(string countryName, string clubName) =>
        _technicalFailures.Add((countryName, clubName));

    // REQ-110: the Club x Club counterpart of FailWithTechnicalFailure above.
    public void FailClubClubWithTechnicalFailure(string clubAName, string clubBName) =>
        _clubClubTechnicalFailures.Add((clubAName, clubBName));

    // REQ-110 (2026-07-28): the next `attempts` LookupAndPersistAsync calls
    // for this pair invoke onTechnicalFailure and return an empty match
    // list; the call after that (and every one thereafter) returns
    // whatever SetMatches configured (or empty if nothing was configured) —
    // distinct from FailWithTechnicalFailure's "every call fails forever."
    // Lets a test express "fails once, succeeds on same-run retry."
    public void FailWithTechnicalFailureForAttempts(string countryName, string clubName, int attempts) =>
        _remainingTechnicalFailureAttempts[(countryName, clubName)] = attempts;

    // REQ-110: the Club x Club counterpart of FailWithTechnicalFailureForAttempts above.
    public void FailClubClubWithTechnicalFailureForAttempts(string clubAName, string clubBName, int attempts) =>
        _clubClubRemainingTechnicalFailureAttempts[(clubAName, clubBName)] = attempts;

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

    // REQ-110 (2026-07-28): see _lastTimeoutTier's own comment above.
    public WikidataQueryTimeoutTier? GetLastTimeoutTier(string countryName, string clubName) =>
        _lastTimeoutTier.TryGetValue((countryName, clubName), out var tier) ? tier : null;

    public WikidataQueryTimeoutTier? GetClubClubLastTimeoutTier(string clubAName, string clubBName) =>
        _clubClubLastTimeoutTier.TryGetValue((clubAName, clubBName), out var tier) ? tier : null;

    public int GetTrophyCountryCallCount(string trophyName, string countryName) =>
        _trophyCountryCallCounts.TryGetValue((trophyName, countryName), out var count) ? count : 0;

    public int GetTrophyClubCallCount(string trophyName, string clubName) =>
        _trophyClubCallCounts.TryGetValue((trophyName, clubName), out var count) ? count : 0;

    public WikidataLookupOrigin? GetTrophyCountryLastOrigin(string trophyName, string countryName) =>
        _trophyCountryLastOrigin.TryGetValue((trophyName, countryName), out var origin) ? origin : null;

    public WikidataLookupOrigin? GetTrophyClubLastOrigin(string trophyName, string clubName) =>
        _trophyClubLastOrigin.TryGetValue((trophyName, clubName), out var origin) ? origin : null;

    // ADR-0061: see _trophyCountryLastIsTeamTrophy's own comment above.
    public bool? GetTrophyCountryLastIsTeamTrophy(string trophyName, string countryName) =>
        _trophyCountryLastIsTeamTrophy.TryGetValue((trophyName, countryName), out var flag) ? flag : null;

    public bool? GetTrophyCountryLastUsesCountryForSportProperty(string trophyName, string countryName) =>
        _trophyCountryLastUsesCountryForSportProperty.TryGetValue((trophyName, countryName), out var flag) ? flag : null;

    public bool? GetTrophyClubLastIsTeamTrophy(string trophyName, string clubName) =>
        _trophyClubLastIsTeamTrophy.TryGetValue((trophyName, clubName), out var flag) ? flag : null;

    public async Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country, ClubDefinition club, WikidataLookupOrigin origin, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        onCalled?.Invoke();
        _callCounts[(country.Name, club.Name)] = GetCallCount(country.Name, club.Name) + 1;
        _lastOrigin[(country.Name, club.Name)] = origin;
        _lastUsesCountryForSportProperty[(country.Name, club.Name)] = country.UsesCountryForSportProperty;
        _lastTimeoutTier[(country.Name, club.Name)] = timeoutTier;

        if (_timeoutFailures.Contains((country.Name, club.Name)))
            throw new WikidataQueryException($"simulated guess-time-fallback timeout for {country.Name}/{club.Name}");

        if (country.WikidataQid is null || club.WikidataQid is null)
            return [];

        // REQ-110 (2026-07-28): the countdown-based failure takes priority
        // over the "always fails" HashSet below — a test configuring both
        // would be a test bug, but if it happens, "fails N times then
        // succeeds" is the more specific/intentional configuration.
        if (_remainingTechnicalFailureAttempts.TryGetValue((country.Name, club.Name), out var remainingAttempts) && remainingAttempts > 0)
        {
            _remainingTechnicalFailureAttempts[(country.Name, club.Name)] = remainingAttempts - 1;
            onTechnicalFailure?.Invoke();
            return [];
        }

        // REQ-110: mirrors WikidataClient's throwOnTimeout=false contract —
        // a technical failure still returns an empty list, but observably so.
        if (_technicalFailures.Contains((country.Name, club.Name)))
        {
            onTechnicalFailure?.Invoke();
            return [];
        }

        if (!_matches.TryGetValue((country.Name, club.Name), out var players))
            return [];

        if (playerOverrideRepository is not null)
        {
            foreach (var player in players)
                await PersistAsync(player, NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
        }

        return players;
    }

    public async Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA, ClubDefinition clubB, WikidataLookupOrigin origin, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        onCalled?.Invoke();
        _clubClubCallCounts[(clubA.Name, clubB.Name)] = GetClubClubCallCount(clubA.Name, clubB.Name) + 1;
        _clubClubLastOrigin[(clubA.Name, clubB.Name)] = origin;
        _clubClubLastTimeoutTier[(clubA.Name, clubB.Name)] = timeoutTier;

        if (clubA.WikidataQid is null || clubB.WikidataQid is null)
            return [];

        // REQ-110: see LookupAndPersistAsync's own comment on this same
        // countdown-takes-priority check.
        if (_clubClubRemainingTechnicalFailureAttempts.TryGetValue((clubA.Name, clubB.Name), out var remainingAttempts) && remainingAttempts > 0)
        {
            _clubClubRemainingTechnicalFailureAttempts[(clubA.Name, clubB.Name)] = remainingAttempts - 1;
            onTechnicalFailure?.Invoke();
            return [];
        }

        // REQ-110: see LookupAndPersistAsync's own comment on this same check.
        if (_clubClubTechnicalFailures.Contains((clubA.Name, clubB.Name)))
        {
            onTechnicalFailure?.Invoke();
            return [];
        }

        if (!_clubClubMatches.TryGetValue((clubA.Name, clubB.Name), out var players))
            return [];

        if (playerOverrideRepository is not null)
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
        // ADR-0061.
        _trophyCountryLastIsTeamTrophy[(trophy.Name, country.Name)] = trophy.IsTeamTrophy;
        _trophyCountryLastUsesCountryForSportProperty[(trophy.Name, country.Name)] = country.UsesCountryForSportProperty;

        if (trophy.WikidataQid is null || country.WikidataQid is null)
            return [];

        if (!_trophyCountryMatches.TryGetValue((trophy.Name, country.Name), out var players))
            return [];

        if (playerOverrideRepository is not null)
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
        // ADR-0061.
        _trophyClubLastIsTeamTrophy[(trophy.Name, club.Name)] = trophy.IsTeamTrophy;

        if (trophy.WikidataQid is null || club.WikidataQid is null)
            return [];

        if (!_trophyClubMatches.TryGetValue((trophy.Name, club.Name), out var players))
            return [];

        if (playerOverrideRepository is not null)
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
            : await playerRepository!.GetPlayerByWikidataQidAsync(player.WikidataQid, cancellationToken);
        var persisted = existing ?? await playerRepository!.AddPlayerAsync(
            new Player { Id = player.Id, FullName = player.FullName, WikidataQid = player.WikidataQid, PhotoUrl = player.PhotoUrl },
            cancellationToken);

        if (!await playerOverrideRepository!.HasEffectiveAttributeAsync(persisted.Id, attributeTypeA, attributeValueA, cancellationToken))
            await playerAttributeRepository!.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = attributeTypeA, AttributeValue = attributeValueA },
                cancellationToken);

        if (!await playerOverrideRepository!.HasEffectiveAttributeAsync(persisted.Id, attributeTypeB, attributeValueB, cancellationToken))
            await playerAttributeRepository!.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = persisted.Id, AttributeType = attributeTypeB, AttributeValue = attributeValueB },
                cancellationToken);
    }
}
