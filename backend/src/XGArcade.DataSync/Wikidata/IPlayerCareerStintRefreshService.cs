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
    // Never throws unless throwOnFailure is true, in which case a Wikidata
    // technical failure is rethrown as WikidataQueryException instead of
    // being logged and swallowed — see throwOnFailure's own doc comment
    // below. With the default false, a Wikidata failure for some or all of
    // playerIds must never fail xG Path round generation (REQ-103's "never
    // block generation on a Wikidata failure" reasoning, applied here to a
    // second game). A player whose refresh fails simply keeps whatever
    // PlayerCareerStint rows they already had (which may be incomplete, but
    // is never worse than before this call) — see the implementation's own
    // doc comment.
    //
    // throwOnFailure (REQ-1404, 2026-09-02 fix, S-211 architecture-review
    // follow-up): defaults to false, which preserves this method's original
    // swallow-and-log contract completely unchanged for xG Path — every
    // existing caller (XGPathGameModule.GenerateInstanceAsync) is
    // unaffected. Set to true ONLY by a caller that needs a genuine Wikidata
    // technical failure to be distinguishable from "this player really has
    // no career data" — currently PlayerCareerOverlapService
    // (Games.XGConnect), whose REQ-1404 LiveLookupUnavailable outcome
    // depends on that distinction the same way REQ-211's guess-time
    // fallback needs IWikidataClient's own throwOnTimeout parameter (see
    // that parameter's own doc comment in IWikidataClient.cs for the
    // identical shape this mirrors).
    Task RefreshCareerStintsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default);
}
