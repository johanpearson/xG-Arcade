namespace XGArcade.DataSync.Tests.Wikidata;

// Minimal hand-rolled TimeProvider fake, same pattern as
// XGArcade.Core.Tests.Rounds.FixedTimeProvider and
// XGArcade.Games.XGGrid.Tests.ManualTimeProvider — pins ADR-0025's rolling
// 100-year-old date-of-birth cutoff to a deterministic instant instead of
// tolerating real wall-clock drift in test assertions.
internal class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
