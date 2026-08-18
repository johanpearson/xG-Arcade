using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-037: StaleClubAttributeCleaner recovers from a wrong Wikidata QID
// discovered after real PlayerAttribute/PlayerData rows were already
// fetched under it — see that class's own doc comment and NOTES.md's
// 2026-07-13 entry for the real incident this responds to (4 of S-036's
// club QIDs were wrong; each happened to be some *other* real Wikidata
// entity, so queries against them didn't error or return empty, they
// silently returned real-but-wrong player data under the intended club's
// name).
public class StaleClubAttributeCleanerTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<Player> SeedPlayerWithClubAttributeAsync(string clubName, string playerNameSuffix = "")
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {clubName}{playerNameSuffix}", WikidataQid = $"Q{Guid.NewGuid():N}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubName });
        _dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = clubName,
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        return player;
    }

    // The regression case this whole class exists for: a club seeded with
    // a QID that turns out to be some *other* real Wikidata entity doesn't
    // fail loudly (empty results, an error) — it silently returns that
    // other entity's real players, persisted under the intended club's
    // name (here simulated directly, the same shape WikidataLookupService
    // would have produced against the wrong QID). Confirms the actual
    // regression this class guards against: after the QID is corrected,
    // the wrongly-matched data doesn't linger — a subsequent guess/grid-
    // generation lookup against this club name finds zero cached matches,
    // not a silent match against the unrelated entity's leftover data.
    [Test]
    public async Task REQ111_CleanAsync_RemovesDataFetchedUnderAPreviouslyWrongQid_LeavingZeroCachedMatches()
    {
        // Simulates what actually happened for Napoli/AS Roma/Sevilla/Porto:
        // real players persisted under the club's name, fetched while its
        // WikidataQid pointed at the wrong entity.
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Napoli", " Two");

        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(removedAttributeCount, Is.EqualTo(2));
        Assert.That(removedDataCount, Is.EqualTo(2));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Napoli"), Is.EqualTo(0),
            "no cell should ever be able to silently match against data fetched under the wrong QID after it's corrected");
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club" && d.Value == "Napoli"), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAsync_OnlyRemovesTheNamedClubs_LeavesOthersUntouched()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Arsenal");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"), Is.EqualTo(1),
            "cleaning one club's stale data must never touch another club's real data");
    }

    [Test]
    public async Task REQ111_CleanAsync_MultipleClubNamesAtOnce_RemovesAllOfThem()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("AS Roma");
        await SeedPlayerWithClubAttributeAsync("Sevilla");
        await SeedPlayerWithClubAttributeAsync("Porto");
        await SeedPlayerWithClubAttributeAsync("Arsenal");

        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli", "AS Roma", "Sevilla", "Porto"]);

        Assert.That(removedAttributeCount, Is.EqualTo(4));
        Assert.That(removedDataCount, Is.EqualTo(4));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ111_CleanAsync_DoesNotTouchNonClubAttributes()
    {
        var player = await SeedPlayerWithClubAttributeAsync("Napoli");
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "Napoli" });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "nationality" && a.AttributeValue == "Napoli"), Is.EqualTo(1),
            "AttributeType must be scoped to \"club\" specifically — a same-named nationality value (however contrived) must not be swept up");
    }

    [Test]
    public async Task REQ111_CleanAsync_IsSafeToRunAgain_WhenNothingIsLeftToClean()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        var (secondRunRemovedAttributeCount, secondRunRemovedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(secondRunRemovedAttributeCount, Is.EqualTo(0));
        Assert.That(secondRunRemovedDataCount, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAsync_NoMatchingData_ReturnsZero_DoesNotThrow()
    {
        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Nonexistent Club"]);

        Assert.That(removedAttributeCount, Is.EqualTo(0));
        Assert.That(removedDataCount, Is.EqualTo(0));
    }

    // ---- CleanAllSeededClubsAsync (`--all-clubs` mode) ---------------------
    // Added for the truthy-wdt:P54 recovery (see the cleaner's own doc
    // comment): when every seeded club's cached data is suspect at once,
    // the club-name list must come from the ClubDefinition reference table,
    // not a hand-typed ~32-name argument where one typo silently leaves a
    // club stale (indistinguishable from "nothing to clean").

    private async Task SeedClubDefinitionAsync(string name)
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = "Q1" });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_ResolvesNamesFromClubDefinitionTable_RemovesEverySeededClubsData()
    {
        await SeedClubDefinitionAsync("Napoli");
        await SeedClubDefinitionAsync("AC Milan");
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("AC Milan");

        var (removedAttributeCount, removedDataCount, clubNames) = await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(clubNames, Is.EquivalentTo(new[] { "Napoli", "AC Milan" }),
            "the swept club list must be resolved from ClubDefinition at runtime, and reported so the operator can eyeball it");
        Assert.That(removedAttributeCount, Is.EqualTo(2));
        Assert.That(removedDataCount, Is.EqualTo(2));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club"), Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club"), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_MeansAllSeededClubs_NotAllClubAttributeRows()
    {
        // "--all-clubs" is scoped by the reference table, same as the named
        // mode is scoped by its argument — a club attribute value that no
        // ClubDefinition row claims (e.g. legacy data for a since-removed
        // club) is deliberately out of reach of this tool either way.
        await SeedClubDefinitionAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Unseeded Legacy Club");

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Unseeded Legacy Club"), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club" && d.Value == "Unseeded Legacy Club"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_DoesNotTouchNonClubAttributes()
    {
        await SeedClubDefinitionAsync("Napoli");
        var player = await SeedPlayerWithClubAttributeAsync("Napoli");
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "Italy" });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "nationality" && a.AttributeValue == "Italy"), Is.EqualTo(1),
            "recovering club data must never remove nationality attributes — those weren't fetched under the broken club pattern's value semantics");
    }

    [Test]
    public void REQ111_CleanAllSeededClubsAsync_NoClubDefinitionRows_ThrowsInsteadOfSilentlyCleaningNothing()
    {
        // Zero seeded clubs is a wrong-database/never-seeded signal, not a
        // real "nothing to clean" case — a quiet "removed 0 rows" success
        // here would read as recovery-complete while leaving every stale
        // row in place on the intended database.
        Assert.ThrowsAsync<InvalidOperationException>(() => StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext));
    }

    // ---- REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): -
    // CleanAsync/CleanAllSeededClubsAsync must also clear any
    // ConfirmedLowMatchPair row touching a cleaned club — on EITHER side of
    // the composite key (Country x Club's Club side, or either side of
    // Club x Club) — or PlayerCacheWarmingService.WarmAsync would skip
    // re-checking a pair using leftover data from before the correction.

    private async Task SeedConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue, string secondAttributeType, string secondAttributeValue, int matchCount = 0)
    {
        _dbContext.ConfirmedLowMatchPairs.Add(new ConfirmedLowMatchPair
        {
            FirstAttributeType = firstAttributeType,
            FirstAttributeValue = firstAttributeValue,
            SecondAttributeType = secondAttributeType,
            SecondAttributeValue = secondAttributeValue,
            MatchCount = matchCount,
            ConfirmedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ110_CleanAsync_RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide()
    {
        // Mirrors PlayerCacheWarmingService's own Country x Club ordering
        // (nationality first, club second).
        await SeedConfirmedLowAsync("nationality", "France", "club", "Napoli");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.ConfirmedLowMatchPairs.CountAsync(), Is.EqualTo(0),
            "a stale confirmed-low marker for a corrected club must not survive its own cleanup — a later warm-player-cache run would otherwise wrongly skip re-checking it");
    }

    [Test]
    public async Task REQ110_CleanAsync_RemovesConfirmedLowMatchPair_OnEitherSideOfAClubClubPair()
    {
        await SeedConfirmedLowAsync("club", "Napoli", "club", "Arsenal");
        await SeedConfirmedLowAsync("club", "Arsenal", "club", "Napoli");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.ConfirmedLowMatchPairs.CountAsync(), Is.EqualTo(0),
            "a stale Club x Club confirmed-low marker must be cleared regardless of which side (first or second) the corrected club appears on");
    }

    [Test]
    public async Task REQ110_CleanAsync_LeavesConfirmedLowMatchPairsForOtherClubsUntouched()
    {
        await SeedConfirmedLowAsync("nationality", "France", "club", "Napoli");
        await SeedConfirmedLowAsync("nationality", "Spain", "club", "Arsenal");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.ConfirmedLowMatchPairs.CountAsync(), Is.EqualTo(1),
            "cleaning one club's stale confirmed-low markers must never touch another club's real ones");
        Assert.That((await _dbContext.ConfirmedLowMatchPairs.SingleAsync()).SecondAttributeValue, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task REQ110_CleanAllSeededClubsAsync_RemovesConfirmedLowMatchPairsForEverySeededClub()
    {
        await SeedClubDefinitionAsync("Napoli");
        await SeedClubDefinitionAsync("AC Milan");
        await SeedConfirmedLowAsync("nationality", "France", "club", "Napoli");
        await SeedConfirmedLowAsync("club", "AC Milan", "club", "Arsenal");

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.ConfirmedLowMatchPairs.CountAsync(), Is.EqualTo(0),
            "the --all-clubs mode's 'purge and re-warm must force a real re-check' invariant applies to ConfirmedLowMatchPair the same as PlayerAttribute/PlayerData");
    }

    // ---- REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052): CleanAsync/CleanAllSeededClubsAsync must also
    // clear any PairLookupFailure row touching a cleaned club, on EITHER
    // side of the composite key — same invalidation-surface reasoning as
    // ConfirmedLowMatchPair immediately above, and the same risk if a
    // future change to either cleaner forgets it: PlayerCacheWarmingService
    // would keep skipping a pair using a persistent-failure marker left
    // over from before the correction, instead of giving the fix a real
    // chance to prove itself.

    private async Task SeedPairLookupFailureAsync(
        string firstAttributeType, string firstAttributeValue, string secondAttributeType, string secondAttributeValue, int consecutiveFailureCount = 2)
    {
        _dbContext.PairLookupFailures.Add(new PairLookupFailure
        {
            FirstAttributeType = firstAttributeType,
            FirstAttributeValue = firstAttributeValue,
            SecondAttributeType = secondAttributeType,
            SecondAttributeValue = secondAttributeValue,
            ConsecutiveFailureCount = consecutiveFailureCount,
            LastFailedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ110_CleanAsync_RemovesPairLookupFailure_OnACountryClubPairsClubSide()
    {
        await SeedPairLookupFailureAsync("nationality", "France", "club", "Napoli");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0),
            "a stale persistent-failure marker for a corrected club must not survive its own cleanup — a later warm-player-cache run would otherwise wrongly keep skipping it");
    }

    [Test]
    public async Task REQ110_CleanAsync_RemovesPairLookupFailure_OnEitherSideOfAClubClubPair()
    {
        await SeedPairLookupFailureAsync("club", "Napoli", "club", "Arsenal");
        await SeedPairLookupFailureAsync("club", "Arsenal", "club", "Napoli");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0),
            "a stale Club x Club persistent-failure marker must be cleared regardless of which side (first or second) the corrected club appears on");
    }

    [Test]
    public async Task REQ110_CleanAsync_LeavesPairLookupFailuresForOtherClubsUntouched()
    {
        await SeedPairLookupFailureAsync("nationality", "France", "club", "Napoli");
        await SeedPairLookupFailureAsync("nationality", "Spain", "club", "Arsenal");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(1),
            "cleaning one club's stale persistent-failure markers must never touch another club's real ones");
        Assert.That((await _dbContext.PairLookupFailures.SingleAsync()).SecondAttributeValue, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task REQ110_CleanAllSeededClubsAsync_RemovesPairLookupFailuresForEverySeededClub()
    {
        await SeedClubDefinitionAsync("Napoli");
        await SeedClubDefinitionAsync("AC Milan");
        await SeedPairLookupFailureAsync("nationality", "France", "club", "Napoli");
        await SeedPairLookupFailureAsync("club", "AC Milan", "club", "Arsenal");

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0),
            "the --all-clubs mode's 'purge and re-warm must force a real re-check' invariant applies to PairLookupFailure the same as ConfirmedLowMatchPair/PlayerAttribute/PlayerData");
    }

    // ---- REQ-110/ADR-0078/S-160: CleanAsync/CleanAllSeededClubsAsync must
    // also null out the cleaned ClubDefinition row(s)' own PlayerPoolSweptAt
    // — leaving it set would let PlayerCacheWarmingService's
    // confirmed-low-from-sweep short-circuit keep trusting a "fully swept"
    // claim about pool data this cleanup just declared suspect, wrongly
    // suppressing the real re-sweep the correction is meant to make room
    // for. Same invalidation-surface reasoning, and same regression risk,
    // as the ConfirmedLowMatchPair/PairLookupFailure coverage above.

    [Test]
    public async Task REQ110_CleanAsync_NullsOutPlayerPoolSweptAt_OnTheNamedClub()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Napoli", WikidataQid = "Q1", PlayerPoolSweptAt = DateTime.UtcNow });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        var napoli = await _dbContext.ClubDefinitions.SingleAsync(c => c.Name == "Napoli");
        Assert.That(napoli.PlayerPoolSweptAt, Is.Null,
            "a corrected club's stale 'fully swept' claim must not survive its own cleanup — a later prefetch/warm cycle needs a real chance to re-sweep it");
    }

    [Test]
    public async Task REQ110_CleanAsync_LeavesPlayerPoolSweptAtForOtherClubsUntouched()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Napoli", WikidataQid = "Q1", PlayerPoolSweptAt = DateTime.UtcNow });
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q2", PlayerPoolSweptAt = DateTime.UtcNow });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        var arsenal = await _dbContext.ClubDefinitions.SingleAsync(c => c.Name == "Arsenal");
        Assert.That(arsenal.PlayerPoolSweptAt, Is.Not.Null,
            "cleaning one club's stale sweep marker must never touch another club's real one");
    }

    [Test]
    public async Task REQ110_CleanAsync_ClubWithNoClubDefinitionRow_DoesNotThrow()
    {
        // Same "no matching data" tolerance as this cleaner's own
        // REQ111_CleanAsync_NoMatchingData_ReturnsZero_DoesNotThrow test — a
        // club name with no ClubDefinition row at all (e.g. a legacy
        // PlayerAttribute value) must not make the cleanup fail.
        await SeedPlayerWithClubAttributeAsync("Unseeded Legacy Club");

        Assert.DoesNotThrowAsync(() => StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Unseeded Legacy Club"]));
    }

    [Test]
    public async Task REQ110_CleanAllSeededClubsAsync_NullsOutPlayerPoolSweptAt_ForEverySeededClub()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Napoli", WikidataQid = "Q1", PlayerPoolSweptAt = DateTime.UtcNow });
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "AC Milan", WikidataQid = "Q2", PlayerPoolSweptAt = DateTime.UtcNow });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.ClubDefinitions.AllAsync(c => c.PlayerPoolSweptAt == null), Is.True,
            "the --all-clubs mode's 'purge and re-warm must force a real re-check' invariant applies to PlayerPoolSweptAt the same as PlayerAttribute/PlayerData/ConfirmedLowMatchPair/PairLookupFailure");
    }
}
