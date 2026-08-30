using Microsoft.EntityFrameworkCore;
using XGArcade.Api.Predict;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// This story (wiring "xg-predict" into round scheduling): unit coverage for
// PredictTemplateResolver.GetOrCreateByMatchCountAsync, mirroring
// XGArcade.Api.Path's own PathTemplateResolver find-or-create shape (no
// dedicated PathTemplateResolverTests file exists to mirror directly, so
// this follows the same InMemory-DbContext-backed pattern
// PredictInstanceRepositoryTests already establishes for the repository
// methods this resolver calls). InternalsVisibleTo("XGArcade.Api.Tests")
// (XGArcade.Api/AssemblyInfo.cs) is what lets this test project reach the
// internal PredictTemplateResolver class directly.
public class PredictTemplateResolverTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPredictInstanceRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PredictInstanceRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task GetOrCreateByMatchCountAsync_NoExistingTemplate_CreatesOneWithTheGivenMatchCount()
    {
        var result = await PredictTemplateResolver.GetOrCreateByMatchCountAsync(_repository, matchCount: 5, CancellationToken.None);

        Assert.That(result.MatchCount, Is.EqualTo(5));
        Assert.That(await _dbContext.PredictTemplates.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreateByMatchCountAsync_ExistingTemplateWithMatchingMatchCount_ReturnsItRatherThanCreatingASecondOne()
    {
        var existing = new PredictTemplate { Id = Guid.NewGuid(), MatchCount = 5 };
        _dbContext.PredictTemplates.Add(existing);
        await _dbContext.SaveChangesAsync();

        var result = await PredictTemplateResolver.GetOrCreateByMatchCountAsync(_repository, matchCount: 5, CancellationToken.None);

        Assert.That(result.Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.PredictTemplates.CountAsync(), Is.EqualTo(1),
            "an existing template with a matching MatchCount must be reused, never duplicated");
    }

    [Test]
    public async Task GetOrCreateByMatchCountAsync_ExistingTemplateWithDifferentMatchCount_CreatesANewOneForTheRequestedCount()
    {
        _dbContext.PredictTemplates.Add(new PredictTemplate { Id = Guid.NewGuid(), MatchCount = 3 });
        await _dbContext.SaveChangesAsync();

        var result = await PredictTemplateResolver.GetOrCreateByMatchCountAsync(_repository, matchCount: 5, CancellationToken.None);

        Assert.That(result.MatchCount, Is.EqualTo(5));
        Assert.That(await _dbContext.PredictTemplates.CountAsync(), Is.EqualTo(2));
    }
}
