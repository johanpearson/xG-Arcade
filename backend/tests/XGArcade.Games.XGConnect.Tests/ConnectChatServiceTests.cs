using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1410 (docs/requirements-document.md §4.15, ~line 11481): in-match text
// chat send/read. Same no-mocking-framework, real-InMemory-backed-repository
// pattern as ConnectChainStepServiceTests — both IConnectMatchRepository
// (participant/existence check only) and IConnectChatMessageRepository
// (S-208 persistence) are exercised through their real implementations
// against an InMemory-backed XGArcadeDbContext; there is no collaborator here
// that warrants a hand-rolled fake the way
// FakePlayerCareerOverlapService/IConnectMatchLifecycleService do for
// ConnectChainStepService.
//
// Deliberately does NOT re-test ConnectMatch.Status branching for
// send/read — see IConnectChatService's own doc comment for why REQ-1410 has
// no such precondition, unlike REQ-1406/1407.
public class ConnectChatServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private IConnectChatMessageRepository _connectChatMessageRepository = null!;
    private ConnectChatService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
        _connectChatMessageRepository = new ConnectChatMessageRepository(_dbContext);
        _service = new ConnectChatService(_connectMatchRepository, _connectChatMessageRepository, new FixedTimeProvider(FixedNow));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<(ConnectMatch Match, Guid AUserId, Guid BUserId)> CreateMatchAsync(
        ConnectMatchStatus status = ConnectMatchStatus.Active, ConnectMatchOutcome outcome = ConnectMatchOutcome.Pending)
    {
        var aUserId = Guid.NewGuid();
        var bUserId = Guid.NewGuid();
        var match = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = aUserId,
            PlayerBUserId = bUserId,
            CreatedAt = FixedNow.UtcDateTime,
            Status = status,
            Outcome = outcome,
        });
        return (match, aUserId, bUserId);
    }

    // ---- REQ-1410 GWT#1: a message sent by a participant is visible to
    // ---- the OTHER participant, scoped to that match only -------------------

    [Test]
    public async Task REQ1410_SendMessageAsync_Participant_PersistsMessageVisibleToOtherParticipantViaGetMessagesAsync()
    {
        var (match, aUserId, bUserId) = await CreateMatchAsync();

        var sendResult = await _service.SendMessageAsync(match.Id, aUserId, "gl hf");

        Assert.That(sendResult.Outcome, Is.EqualTo(ConnectChatOutcome.Success));
        Assert.That(sendResult.Message, Is.Not.Null);
        Assert.That(sendResult.Message!.MessageText, Is.EqualTo("gl hf"));
        Assert.That(sendResult.Message.SenderUserId, Is.EqualTo(aUserId));

        // The OTHER participant (B), reading the same match, sees it.
        var readResult = await _service.GetMessagesAsync(match.Id, bUserId);

        Assert.That(readResult.Outcome, Is.EqualTo(ConnectChatOutcome.Success));
        Assert.That(readResult.Messages, Has.Count.EqualTo(1));
        Assert.That(readResult.Messages![0].MessageText, Is.EqualTo("gl hf"));
        Assert.That(readResult.Messages[0].SenderUserId, Is.EqualTo(aUserId));
    }

    [Test]
    public async Task REQ1410_GetMessagesAsync_IsScopedToOneMatch_EvenBetweenSameTwoPlayersInADifferentMatch()
    {
        var aUserId = Guid.NewGuid();
        var bUserId = Guid.NewGuid();
        var matchOne = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(), PlayerAUserId = aUserId, PlayerBUserId = bUserId,
            CreatedAt = FixedNow.UtcDateTime, Status = ConnectMatchStatus.Active,
        });
        // Same two players, a SEPARATE match.
        var matchTwo = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(), PlayerAUserId = aUserId, PlayerBUserId = bUserId,
            CreatedAt = FixedNow.UtcDateTime, Status = ConnectMatchStatus.Active,
        });
        await _service.SendMessageAsync(matchOne.Id, aUserId, "match one message");

        var resultForMatchTwo = await _service.GetMessagesAsync(matchTwo.Id, bUserId);

        Assert.That(resultForMatchTwo.Outcome, Is.EqualTo(ConnectChatOutcome.Success));
        Assert.That(resultForMatchTwo.Messages, Is.Empty,
            "a message sent in one match must never be visible in a different match, even between the same two players");

        var resultForMatchOne = await _service.GetMessagesAsync(matchOne.Id, bUserId);
        Assert.That(resultForMatchOne.Messages, Has.Count.EqualTo(1));
    }

    // ---- REQ-1410 GWT#2: chat remains readable (and, per the service's own
    // ---- deliberate no-status-gate, sendable) once a match has reached a
    // ---- terminal state for both players --------------------------------------

    [Test]
    public async Task REQ1410_GetMessagesAsync_MatchResolved_StillReturnsMessages()
    {
        var (match, aUserId, bUserId) = await CreateMatchAsync();
        await _service.SendMessageAsync(match.Id, aUserId, "before resolution");
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.PlayerAWin, FixedNow.UtcDateTime, 3, null);

        var result = await _service.GetMessagesAsync(match.Id, bUserId);

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.Success));
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        Assert.That(result.Messages![0].MessageText, Is.EqualTo("before resolution"));
    }

    [Test]
    public async Task REQ1410_SendMessageAsync_MatchResolved_StillSucceeds()
    {
        var (match, aUserId, _) = await CreateMatchAsync();
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.Draw, FixedNow.UtcDateTime, null, null);

        var result = await _service.SendMessageAsync(match.Id, aUserId, "gg");

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.Success),
            "REQ-1410 never makes match status a precondition for sending, unlike REQ-1406/1407");
        Assert.That(result.Message!.MessageText, Is.EqualTo("gg"));
    }

    // ---- REQ-1410 GWT#3: a non-participant is rejected, both directions -----

    [Test]
    public async Task REQ1410_SendMessageAsync_NonParticipant_ReturnsNotAParticipant_PersistsNothing()
    {
        var (match, _, _) = await CreateMatchAsync();
        var outsider = Guid.NewGuid();

        var result = await _service.SendMessageAsync(match.Id, outsider, "let me in");

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.NotAParticipant));
        Assert.That(result.Message, Is.Null);
        Assert.That(await _connectChatMessageRepository.GetMessagesForMatchAsync(match.Id), Is.Empty);
    }

    [Test]
    public async Task REQ1410_GetMessagesAsync_NonParticipant_ReturnsNotAParticipant()
    {
        var (match, aUserId, _) = await CreateMatchAsync();
        await _service.SendMessageAsync(match.Id, aUserId, "private chat");
        var outsider = Guid.NewGuid();

        var result = await _service.GetMessagesAsync(match.Id, outsider);

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.NotAParticipant));
        Assert.That(result.Messages, Is.Null);
    }

    // ---- Mechanical precondition: MatchNotFound, both directions ------------

    [Test]
    public async Task REQ1410_SendMessageAsync_MatchNotFound_ReturnsMatchNotFound()
    {
        var result = await _service.SendMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "anyone home?");

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.MatchNotFound));
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public async Task REQ1410_GetMessagesAsync_MatchNotFound_ReturnsMatchNotFound()
    {
        var result = await _service.GetMessagesAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ConnectChatOutcome.MatchNotFound));
        Assert.That(result.Messages, Is.Null);
    }
}
