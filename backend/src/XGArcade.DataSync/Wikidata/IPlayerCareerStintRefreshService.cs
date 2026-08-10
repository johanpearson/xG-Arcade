namespace XGArcade.DataSync.Wikidata;

// ADR-0054: xG Path's own direct career fetch — refreshes a small, specific
// batch of players' PlayerCareerStint rows from Wikidata's full P54 history,
// independent of whatever xG Grid's own country/club lookups have happened
// to persist so far (ADR-0042's byproduct model). Interface exists (rather
// than XGPathGameModule depending on the concrete class directly) so
// XGPathGameModuleTests can substitute a hand-rolled fake, the same "no
// mocking framework" pattern FakeWikidataLookupService already establishes
// for Games.XGGrid.Tests.
public interface IPlayerCareerStintRefreshService
{
    // Never throws — a Wikidata failure for some or all of playerIds must
    // never fail xG Path round generation (REQ-103's "never block generation
    // on a Wikidata failure" reasoning, applied here to a second game). A
    // player whose refresh fails simply keeps whatever PlayerCareerStint
    // rows they already had (which may be incomplete, but is never worse
    // than before this call) — see the implementation's own doc comment.
    Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken = default);
}
