using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// REQ-511 (docs/requirements-document.md): the singleton-upsert-not-insert
// behavior AnnouncementBanner.cs's own doc comment calls out — "the first
// admin write creates it, and every write after that (edit, activate,
// deactivate) mutates the same row in place... never inserts a second row"
// — is exactly the kind of repository-layer logic worth a dedicated unit
// test per implementation-document.md §7, distinct from the API-level
// coverage in XGArcade.Api.Tests/AnnouncementBannerEndpointTests.cs. Same
// EF Core InMemory-provider unit-test shape as CategoryValueRepositoryTests.
public class AnnouncementBannerRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IAnnouncementBannerRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new AnnouncementBannerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task REQ511_GetAsync_ReturnsNull_WhenNoBannerHasEverBeenCreated()
    {
        var banner = await _repository.GetAsync();

        Assert.That(banner, Is.Null);
    }

    [Test]
    public async Task REQ511_UpsertMessageAsync_CreatesTheRow_WhenNoneExistsYet_StartingInactive()
    {
        var adminId = Guid.NewGuid();
        var updatedAt = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        var banner = await _repository.UpsertMessageAsync("Scheduled maintenance tonight.", adminId, updatedAt);

        Assert.That(banner.Message, Is.EqualTo("Scheduled maintenance tonight."));
        // AnnouncementBanner.cs's own doc comment: "A newly-created row
        // starts IsActive=false" — never active by default.
        Assert.That(banner.IsActive, Is.False);
        Assert.That(banner.CreatedAt, Is.EqualTo(updatedAt));
        Assert.That(banner.UpdatedAt, Is.EqualTo(updatedAt));
        Assert.That(banner.LastUpdatedByAdminId, Is.EqualTo(adminId));
        Assert.That(await _dbContext.AnnouncementBanners.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ511_UpsertMessageAsync_ReplacesTheExistingRowsMessage_RatherThanInsertingASecondRow()
    {
        var firstAdminId = Guid.NewGuid();
        var secondAdminId = Guid.NewGuid();
        var created = await _repository.UpsertMessageAsync(
            "Original message.", firstAdminId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var updated = await _repository.UpsertMessageAsync(
            "Replacement message.", secondAdminId, new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(await _dbContext.AnnouncementBanners.CountAsync(), Is.EqualTo(1),
            "REQ-511: a second create/edit replaces the single existing banner, it does not create an additional one");
        Assert.That(updated.Id, Is.EqualTo(created.Id), "the same row is mutated in place, never replaced by a new one");
        Assert.That(updated.Message, Is.EqualTo("Replacement message."));
        Assert.That(updated.LastUpdatedByAdminId, Is.EqualTo(secondAdminId));
        Assert.That(updated.UpdatedAt, Is.EqualTo(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task REQ511_UpsertMessageAsync_EditingAnAlreadyActiveBanner_LeavesIsActiveUntouched()
    {
        var adminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Original message.", adminId, DateTime.UtcNow);
        await _repository.SetActiveAsync(true, adminId, DateTime.UtcNow);

        var edited = await _repository.UpsertMessageAsync("Edited while still live.", adminId, DateTime.UtcNow);

        Assert.That(edited.IsActive, Is.True,
            "REQ-511: an edit to an already-active banner does not require a separate deactivate/reactivate step");
        Assert.That(edited.Message, Is.EqualTo("Edited while still live."));
    }

    [Test]
    public async Task REQ511_UpsertMessageAsync_EditingAnInactiveBanner_DoesNotActivateIt()
    {
        var adminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Original message.", adminId, DateTime.UtcNow);

        var edited = await _repository.UpsertMessageAsync("Edited while still inactive.", adminId, DateTime.UtcNow);

        Assert.That(edited.IsActive, Is.False, "editing an inactive banner must not implicitly activate it");
    }

    [Test]
    public async Task REQ511_SetActiveAsync_ReturnsNull_AndWritesNothing_WhenNoBannerRowExistsYet()
    {
        var result = await _repository.SetActiveAsync(true, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result, Is.Null);
        Assert.That(await _dbContext.AnnouncementBanners.CountAsync(), Is.EqualTo(0),
            "there is nothing to activate/deactivate until an admin has created a banner via UpsertMessageAsync");
    }

    [Test]
    public async Task REQ511_SetActiveAsync_True_ActivatesTheBanner_WithoutTouchingItsMessage()
    {
        var adminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Scheduled maintenance tonight.", adminId, DateTime.UtcNow);

        var activated = await _repository.SetActiveAsync(true, adminId, DateTime.UtcNow);

        Assert.That(activated, Is.Not.Null);
        Assert.That(activated!.IsActive, Is.True);
        Assert.That(activated.Message, Is.EqualTo("Scheduled maintenance tonight."));
    }

    [Test]
    public async Task REQ511_SetActiveAsync_False_DeactivatesTheBanner_ButNeverClearsTheSavedMessage()
    {
        var adminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Scheduled maintenance tonight.", adminId, DateTime.UtcNow);
        await _repository.SetActiveAsync(true, adminId, DateTime.UtcNow);

        var deactivated = await _repository.SetActiveAsync(false, adminId, DateTime.UtcNow);

        Assert.That(deactivated, Is.Not.Null);
        Assert.That(deactivated!.IsActive, Is.False);
        // REQ-511: "deactivating does not delete the banner's saved
        // message — an admin can reactivate the same text later, or edit
        // it first, without retyping it from scratch."
        Assert.That(deactivated.Message, Is.EqualTo("Scheduled maintenance tonight."),
            "REQ-511: deactivating must never clear/delete the saved message");
        Assert.That(await _dbContext.AnnouncementBanners.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ511_SetActiveAsync_StampsLastUpdatedByAdminIdAndUpdatedAt_OnEveryToggle()
    {
        var creatingAdminId = Guid.NewGuid();
        var togglingAdminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Text.", creatingAdminId, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var toggleTime = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

        var activated = await _repository.SetActiveAsync(true, togglingAdminId, toggleTime);

        Assert.That(activated!.LastUpdatedByAdminId, Is.EqualTo(togglingAdminId));
        Assert.That(activated.UpdatedAt, Is.EqualTo(toggleTime));
        // CreatedAt must never change on a later write, activation included.
        Assert.That(activated.CreatedAt, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task REQ511_GetAsync_ReflectsTheLatestWrite_RegardlessOfWhichRepositoryMethodMadeIt()
    {
        var adminId = Guid.NewGuid();
        await _repository.UpsertMessageAsync("Original.", adminId, DateTime.UtcNow);
        await _repository.SetActiveAsync(true, adminId, DateTime.UtcNow);

        var fetched = await _repository.GetAsync();

        Assert.That(fetched, Is.Not.Null);
        Assert.That(fetched!.Message, Is.EqualTo("Original."));
        Assert.That(fetched.IsActive, Is.True);
    }
}
