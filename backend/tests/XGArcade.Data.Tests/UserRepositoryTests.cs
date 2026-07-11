using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-017 (docs/backlog.md): case-insensitive DisplayName uniqueness
// (REQ-701/REQ-401). Covers UserRepository.DisplayNameExistsAsync directly,
// at the repository level rather than through AuthController — same
// in-memory-EF pattern as UserDisplayNameBackfillerTests.
//
// UserRepository.AddAsync's DbUpdateException/DisplayNameAlreadyInUseException
// catch (the race-condition fallback behind this pre-check, matched on
// IX_Users_NormalizedDisplayName and Npgsql's PostgresException) is
// deliberately NOT covered here: the InMemory provider used by every test in
// this project does not enforce unique indexes at all, so a test seeding two
// users with colliding NormalizedDisplayName values would simply succeed
// (SaveChangesAsync never throws), making any assertion of the catch
// behavior impossible to write in a way that could actually fail against
// wrong code. That catch path was manually verified against a real local
// Postgres 16 instance when the migration was authored (see docs/backlog.md
// S-017); it would need a real-Postgres-backed integration test tier to
// cover automatically, which does not exist yet in this project.
public class UserRepositoryTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IUserRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new UserRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    [Test]
    public async Task REQ701_DisplayNameExistsAsync_ReturnsTrue_ForExactCaseMatch()
    {
        _dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var exists = await _repository.DisplayNameExistsAsync("Test Player");

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task REQ701_DisplayNameExistsAsync_ReturnsTrue_ForDifferentCasingOfSameName()
    {
        _dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var exists = await _repository.DisplayNameExistsAsync("TEST PLAYER");

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task REQ701_DisplayNameExistsAsync_ReturnsFalse_ForUnrelatedName()
    {
        _dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var exists = await _repository.DisplayNameExistsAsync("Someone Else");

        Assert.That(exists, Is.False);
    }

    // REQ-701 is explicit that only casing is normalized — spaces/punctuation
    // stay exactly as entered, deliberately not reshaped into a
    // username-style field. A name differing only by whitespace is therefore
    // NOT a collision.
    [Test]
    public async Task REQ701_DisplayNameExistsAsync_ReturnsFalse_ForNameDifferingOnlyByWhitespace()
    {
        _dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var exists = await _repository.DisplayNameExistsAsync("Test Player ");

        Assert.That(exists, Is.False);
    }
}
