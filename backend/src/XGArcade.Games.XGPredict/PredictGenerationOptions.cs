namespace XGArcade.Games.XGPredict;

// REQ-1301's fixed match count. Mirrors GridGenerationOptions'
// (XGArcade.Games.XGGrid)/PathGenerationOptions' (XGArcade.Games.XGPath)
// shape/precedent exactly — one small options class per game module holding
// that game's own generation-time config, registered as a DI singleton in
// ServiceRegistration.cs.
//
// This deliberately does NOT live on Core.Rounds' RoundSchedulingOptions
// (see that type's own doc comment) — MatchCount is xG-Predict-specific
// generation config, not a generic round-scheduling concern every GameKey
// shares, the same reasoning that moved GridSize/PuzzleCount off
// RoundSchedulingOptions and onto GridGenerationOptions/PathGenerationOptions
// (S-084). See ADR-0051's 2026-08-30 amendment (xG Predict wiring) for the
// re-derivation confirming this pattern still holds for a third GameKey.
public class PredictGenerationOptions
{
    // Tier 0 has no admin-driven PredictTemplate management yet (mirrors
    // PathGenerationOptions' own precedent) — PredictTemplateResolver
    // find-or-creates a template of this match count on demand, same
    // pattern GridTemplateResolver/PathTemplateResolver already use for
    // GridTemplate.Size/PathTemplate.PuzzleCount.
    public int MatchCount { get; set; } = 5;
}
