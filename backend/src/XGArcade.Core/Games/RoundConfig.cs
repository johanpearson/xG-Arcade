namespace XGArcade.Core.Games;

// Opaque to Core, mirroring Round.GameInstanceId's opaqueness in the other
// direction (ADR-0003): TemplateId means nothing to Core — it's whatever
// identifier the owning game module needs to look up its own generation
// config (for xG Grid, a GridTemplate id). Core never inspects it.
public class RoundConfig
{
    public required Guid TemplateId { get; set; }

    // ADR-0102: populated by RoundGenerationService itself (from the
    // existing GameKey's `latest` Round's GameInstanceId), immediately
    // before calling IGameModule.GenerateInstanceAsync — never set by a
    // caller of GenerateNextRoundIfNeededAsync. Null on a GameKey's
    // first-ever round (no `latest` exists yet). Still opaque to Core in
    // the sense that Core never inspects what the id points to — it only
    // threads a previously-generated GameInstance's own Id back to the
    // module that produced it, so that module can decide (its own opaque
    // logic) whether a new instance is actually due. xg-grid/xg-path never
    // read this field.
    public Guid? LatestGameInstanceId { get; set; }
}
