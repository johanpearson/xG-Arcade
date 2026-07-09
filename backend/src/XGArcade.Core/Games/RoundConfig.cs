namespace XGArcade.Core.Games;

// Opaque to Core, mirroring Round.GameInstanceId's opaqueness in the other
// direction (ADR-0003): TemplateId means nothing to Core — it's whatever
// identifier the owning game module needs to look up its own generation
// config (for xG Grid, a GridTemplate id). Core never inspects it.
public class RoundConfig
{
    public required Guid TemplateId { get; set; }
}
