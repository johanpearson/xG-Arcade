namespace XGArcade.DataSync.Wikidata;

// ADR-0056: the narrow abstraction XGPathGameModule depends on for its
// familiarity check, instead of IWikidataClient directly — same "Games.XGPath
// depends on a small, purpose-built service, never the raw Wikidata client"
// boundary IPlayerCareerStintRefreshService already establishes for the
// career-stint refresh (ADR-0054's own doc comment). Keeps XGPathGameModule's
// test fixture small (implement one narrow method, not all of
// IWikidataClient) and keeps "how familiarity is decided" swappable without
// touching the game module itself.
public interface IPlayerFamiliarityService
{
    // Given a pool of structurally-eligible candidate player ids (REQ-1201's
    // existing checks already passed), returns the subset judged "familiar
    // enough" to be a fair xG Path target (ADR-0056). Never throws — a
    // Wikidata failure must not block round generation (REQ-103's
    // established reasoning); see PlayerFamiliarityService's own doc comment
    // for the fail-open contract this method follows when the underlying
    // sitelink lookup can't complete.
    Task<IReadOnlySet<Guid>> FilterFamiliarAsync(
        IReadOnlyList<Guid> candidatePlayerIds, CancellationToken cancellationToken = default);
}
