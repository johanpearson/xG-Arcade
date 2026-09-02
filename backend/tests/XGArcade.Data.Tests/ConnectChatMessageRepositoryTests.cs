using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// Games.XGConnect (COMP-17)/ADR-0103, S-208: ConnectChatMessageRepository's
// basic persistence round-trips. Schema + CRUD only.
public class ConnectChatMessageRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IConnectChatMessageRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new ConnectChatMessageRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddMessageAsync_ThenGetMessagesForMatchAsync_PersistsAndRetrievesTheRow()
    {
        var matchId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var sentAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var message = new ConnectChatMessage
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            SenderUserId = senderId,
            MessageText = "gl hf",
            SentAt = sentAt,
        };

        var added = await _repository.AddMessageAsync(message);

        Assert.That(added, Is.SameAs(message));
        var result = await _repository.GetMessagesForMatchAsync(matchId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SenderUserId, Is.EqualTo(senderId));
        Assert.That(result[0].MessageText, Is.EqualTo("gl hf"));
    }

    [Test]
    public async Task GetMessagesForMatchAsync_ReturnsInChronologicalOrder()
    {
        var matchId = Guid.NewGuid();
        var later = new ConnectChatMessage
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, SenderUserId = Guid.NewGuid(),
            MessageText = "second", SentAt = new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc),
        };
        var earlier = new ConnectChatMessage
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, SenderUserId = Guid.NewGuid(),
            MessageText = "first", SentAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        // Added out of chronological order, deliberately, so the assertion
        // can't pass by coincidence of insertion order.
        await _repository.AddMessageAsync(later);
        await _repository.AddMessageAsync(earlier);

        var result = await _repository.GetMessagesForMatchAsync(matchId);

        Assert.That(result.Select(m => m.MessageText), Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public async Task GetMessagesForMatchAsync_IsScopedToOneMatch()
    {
        var matchId = Guid.NewGuid();
        var otherMatchId = Guid.NewGuid();
        await _repository.AddMessageAsync(new ConnectChatMessage
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, SenderUserId = Guid.NewGuid(), MessageText = "in match", SentAt = DateTime.UtcNow,
        });
        await _repository.AddMessageAsync(new ConnectChatMessage
        {
            Id = Guid.NewGuid(), ConnectMatchId = otherMatchId, SenderUserId = Guid.NewGuid(), MessageText = "other match", SentAt = DateTime.UtcNow,
        });

        var result = await _repository.GetMessagesForMatchAsync(matchId);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MessageText, Is.EqualTo("in match"));
    }

    [Test]
    public async Task GetMessagesForMatchAsync_NoMessages_ReturnsEmpty()
    {
        var result = await _repository.GetMessagesForMatchAsync(Guid.NewGuid());

        Assert.That(result, Is.Empty);
    }
}
