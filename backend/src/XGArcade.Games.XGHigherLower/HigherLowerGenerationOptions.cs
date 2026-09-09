namespace XGArcade.Games.XGHigherLower;

// REQ-1503's fixed comparator count, defaulting to 10, plus REQ-1502's
// generation-time retry bound. Mirrors GridGenerationOptions'/
// PathGenerationOptions'/PredictGenerationOptions' exact shape/precedent —
// one small options class per game module holding that game's own
// generation-time config, registered as a DI singleton in
// ServiceRegistration.cs.
//
// This deliberately does NOT live on Core.Rounds' RoundSchedulingOptions
// (see that type's own doc comment) — ComparatorCount is xG-Higher/Lower-
// specific generation config, not a generic round-scheduling concern every
// GameKey shares, the same reasoning that moved GridSize/PuzzleCount/
// MatchCount off RoundSchedulingOptions and onto GridGenerationOptions/
// PathGenerationOptions/PredictGenerationOptions (S-084, ADR-0051's
// 2026-08-30 amendment). REQ-1503 is explicit that ComparatorCount does
// NOT go on RoundSchedulingOptions.
public class HigherLowerGenerationOptions
{
    // REQ-1503: the Round's fixed sequence is one baseline plus this many
    // further comparators.
    public int ComparatorCount { get; set; } = 10;

    // REQ-1502's bound on retrying a candidate category (a fresh baseline/
    // shuffle attempt) before giving up on that category and moving to the
    // next candidate — mirrors GridGenerationOptions.MaxAttempts' role as a
    // backstop. Unlike GridGenerationOptions.MaxDuration (ADR-0023), there
    // is no wall-clock deadline here: xG Higher/Lower's generation never
    // makes a live external lookup (everything read is already-cached
    // PlayerAttribute/PlayerOverride data via
    // IPlayerOverrideRepository.GetEffectivePlayerCountsByAttributeTypeAsync),
    // so a bounded attempt count alone is sufficient to guarantee
    // termination.
    public int MaxAttemptsPerCategory { get; set; } = 20;
}
