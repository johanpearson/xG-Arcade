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

    // REQ-506 (S-026): GetByEmailAsync resolves an admin-supplied email to a
    // User.Id for DELETE /admin/users — case-insensitive, matching how
    // Supabase Auth itself treats email, so an admin retyping a differently-
    // cased address doesn't get a spurious "not found."
    [Test]
    public async Task REQ506_GetByEmailAsync_ReturnsUser_ForDifferentCasingOfSameEmail()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "Player@Example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var found = await _repository.GetByEmailAsync("player@example.com");

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Id, Is.EqualTo(user.Id));
    }

    [Test]
    public async Task REQ506_GetByEmailAsync_ReturnsNull_ForUnknownEmail()
    {
        var found = await _repository.GetByEmailAsync("nobody@example.com");

        Assert.That(found, Is.Null);
    }

    // REQ-717/ADR-0036: a guest row (Email = null) must never match a real
    // email lookup — guards against a null-reference-style failure now that
    // Email is nullable, not just a behavioral nicety.
    [Test]
    public async Task REQ717_GetByEmailAsync_NeverMatchesAGuestRowWithNullEmail()
    {
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest4242",
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var found = await _repository.GetByEmailAsync("anything@example.com");

        Assert.That(found, Is.Null);
    }

    // REQ-714: edit display name from Settings. DisplayNameExistsAsync's new
    // excludeUserId parameter — the mechanism AuthController.UpdateDisplayName
    // relies on so a no-op/casing-only resubmission of the caller's own
    // current name is never treated as a conflict against itself.
    [Test]
    public async Task REQ714_DisplayNameExistsAsync_ReturnsFalse_WhenOnlyMatchIsTheExcludedUsersOwnRow()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        // Even a pure-casing resubmission of the caller's own name must not
        // be reported as a conflict once that caller's own row is excluded.
        var exists = await _repository.DisplayNameExistsAsync("TEST PLAYER", excludeUserId: user.Id);

        Assert.That(exists, Is.False);
    }

    [Test]
    public async Task REQ714_DisplayNameExistsAsync_ReturnsTrue_WhenMatchIsADifferentUsersRow_EvenWithExcludeUserIdSet()
    {
        var otherUser = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "other-owner@example.com",
            DisplayName = "Taken Name",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(otherUser);
        await _dbContext.SaveChangesAsync();

        var callerId = Guid.NewGuid();
        var exists = await _repository.DisplayNameExistsAsync("taken name", excludeUserId: callerId);

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task REQ714_UpdateDisplayNameAsync_UpdatesDisplayNameAndKeepsNormalizedDisplayNameInLockstep()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Old Name",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var updated = await _repository.UpdateDisplayNameAsync(user.Id, "New Name");

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.DisplayName, Is.EqualTo("New Name"));
        Assert.That(updated.NormalizedDisplayName, Is.EqualTo("new name"));

        var reloaded = await _repository.GetByIdAsync(user.Id);
        Assert.That(reloaded!.DisplayName, Is.EqualTo("New Name"));
    }

    [Test]
    public async Task REQ714_UpdateDisplayNameAsync_ReturnsNull_ForUnknownUserId()
    {
        var updated = await _repository.UpdateDisplayNameAsync(Guid.NewGuid(), "Doesn't Matter");

        Assert.That(updated, Is.Null);
    }

    // REQ-717/ADR-0036: the claim/upgrade path — an in-place conversion of a
    // guest row, not a new row.
    [Test]
    public async Task REQ717_ClaimGuestAsync_SetsEmailClearsIsGuestAndStampsClaimedAt()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest1234",
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var updated = await _repository.ClaimGuestAsync(user.Id, "claimed@example.com");
        var after = DateTime.UtcNow;

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.Email, Is.EqualTo("claimed@example.com"));
        Assert.That(updated.IsGuest, Is.False);
        Assert.That(updated.ClaimedAt, Is.Not.Null);
        Assert.That(updated.ClaimedAt!.Value, Is.InRange(before, after));

        var reloaded = await _repository.GetByIdAsync(user.Id);
        Assert.That(reloaded!.Email, Is.EqualTo("claimed@example.com"));
        Assert.That(reloaded.IsGuest, Is.False);
    }

    [Test]
    public async Task REQ717_ClaimGuestAsync_ReturnsNull_ForUnknownUserId()
    {
        var updated = await _repository.ClaimGuestAsync(Guid.NewGuid(), "doesnt-matter@example.com");

        Assert.That(updated, Is.Null);
    }

    // ---- REQ-718/ADR-0038: activity tracking + the purge job's two selection queries ----

    // ClaimGuestAsync is one of REQ-718's four activity-tracking events —
    // folded into this same load-then-save rather than a second
    // UpdateLastActiveAtAsync round trip (see IUserRepository's own doc
    // comment on ClaimGuestAsync).
    [Test]
    public async Task REQ718_ClaimGuestAsync_StampsLastActiveAtToNow()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest1234",
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastActiveAt = DateTime.UtcNow.AddDays(-1),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var updated = await _repository.ClaimGuestAsync(user.Id, "claimed@example.com");
        var after = DateTime.UtcNow;

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.LastActiveAt, Is.InRange(before, after));
    }

    [Test]
    public async Task REQ718_UpdateLastActiveAtAsync_SetsLastActiveAtToNow_ForExistingUser()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            LastActiveAt = DateTime.UtcNow.AddDays(-10),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var updated = await _repository.UpdateLastActiveAtAsync(user.Id);
        var after = DateTime.UtcNow;

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.LastActiveAt, Is.InRange(before, after));

        var reloaded = await _repository.GetByIdAsync(user.Id);
        Assert.That(reloaded!.LastActiveAt, Is.InRange(before, after));
    }

    [Test]
    public async Task REQ718_UpdateLastActiveAtAsync_ReturnsNull_ForUnknownUserId()
    {
        var updated = await _repository.UpdateLastActiveAtAsync(Guid.NewGuid());

        Assert.That(updated, Is.Null);
    }

    // Rule 2 (30-day unclaimed purge): IsGuest AND ClaimedAt IS NULL AND
    // CreatedAt < cutoff.
    [Test]
    public async Task REQ718_GetUnclaimedGuestsOlderThanAsync_ReturnsUnclaimedGuest_WhenCreatedBeforeCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest1111",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff.AddDays(-1),
            LastActiveAt = cutoff.AddDays(-1),
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetUnclaimedGuestsOlderThanAsync(cutoff);

        Assert.That(result.Select(u => u.Id), Is.EquivalentTo(new[] { guest.Id }));
    }

    // Boundary: exactly at the cutoff must NOT be treated as "more than 30
    // days" — the query uses a strict `<`.
    [Test]
    public async Task REQ718_GetUnclaimedGuestsOlderThanAsync_ExcludesUnclaimedGuest_WhenCreatedExactlyAtCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest2222",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff,
            LastActiveAt = cutoff,
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetUnclaimedGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ718_GetUnclaimedGuestsOlderThanAsync_ExcludesUnclaimedGuest_WhenCreatedAfterCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest3333",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff.AddDays(1),
            LastActiveAt = cutoff.AddDays(1),
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetUnclaimedGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }

    // REQ-718's own scope note: a claimed account is never purged by rule 2
    // no matter how old CreatedAt is.
    [Test]
    public async Task REQ718_GetUnclaimedGuestsOlderThanAsync_ExcludesClaimedAccount_RegardlessOfAge()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var claimed = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "claimed@example.com",
            DisplayName = "Claimed Player",
            EmailConfirmed = true,
            IsGuest = false,
            ClaimedAt = cutoff.AddDays(-40),
            CreatedAt = cutoff.AddDays(-100),
            LastActiveAt = cutoff.AddDays(-100),
        };
        _dbContext.Users.Add(claimed);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetUnclaimedGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }

    // Rule 3 (7-day inactivity purge): IsGuest AND LastActiveAt < cutoff —
    // deliberately no ClaimedAt condition (claiming already clears IsGuest).
    [Test]
    public async Task REQ718_GetInactiveGuestsOlderThanAsync_ReturnsGuest_WhenLastActiveAtBeforeCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest4444",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff.AddDays(-1),
            LastActiveAt = cutoff.AddDays(-1),
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetInactiveGuestsOlderThanAsync(cutoff);

        Assert.That(result.Select(u => u.Id), Is.EquivalentTo(new[] { guest.Id }));
    }

    [Test]
    public async Task REQ718_GetInactiveGuestsOlderThanAsync_ExcludesGuest_WhenLastActiveAtExactlyAtCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest5555",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff,
            LastActiveAt = cutoff,
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetInactiveGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ718_GetInactiveGuestsOlderThanAsync_ExcludesGuest_WhenLastActiveAtAfterCutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var guest = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = "Guest6666",
            EmailConfirmed = false,
            IsGuest = true,
            ClaimedAt = null,
            CreatedAt = cutoff.AddDays(1),
            LastActiveAt = cutoff.AddDays(1),
        };
        _dbContext.Users.Add(guest);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetInactiveGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }

    // A claimed account must never appear here regardless of how old/stale
    // LastActiveAt later becomes — claiming already cleared IsGuest, which
    // is the only condition this query checks alongside LastActiveAt.
    [Test]
    public async Task REQ718_GetInactiveGuestsOlderThanAsync_ExcludesClaimedAccount_RegardlessOfLastActiveAtAge()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var claimed = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "claimed-2@example.com",
            DisplayName = "Claimed Player 2",
            EmailConfirmed = true,
            IsGuest = false,
            ClaimedAt = cutoff.AddDays(-1),
            CreatedAt = cutoff.AddDays(-50),
            LastActiveAt = cutoff.AddDays(-50),
        };
        _dbContext.Users.Add(claimed);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetInactiveGuestsOlderThanAsync(cutoff);

        Assert.That(result, Is.Empty);
    }
}
