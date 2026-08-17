namespace XGArcade.Games.XGGrid;

// ADR-0070/S-128: an operational toggle for REQ-211's guess-time live-lookup
// fallback (GridGameModule.ScoreSubmissionAsync) only — never for REQ-103's
// grid-generation-time live lookup (IGridLiveLookupDispatcher
// .LookupMatchesAsync / GridGenerationService.GetMatchCountAsync), which
// this options class does not gate at all. Default true so every existing
// caller/test that doesn't construct this explicitly sees unchanged
// behavior; flip to false only to validate S-127's proactively-built cache
// on its own, with an immediate way back via config, no redeploy needed
// beyond a restart. See ADR-0070 for the full "why a flag, not a removal"
// reasoning.
public class GridLiveLookupOptions
{
    public bool Enabled { get; set; } = true;
}
