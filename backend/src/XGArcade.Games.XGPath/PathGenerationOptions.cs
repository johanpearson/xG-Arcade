namespace XGArcade.Games.XGPath;

// REQ-1202's configurable puzzle count. Mirrors GridGenerationOptions'
// (XGArcade.Games.XGGrid) shape/precedent exactly — one small options class
// per game module holding that game's own generation-time config, registered
// as a DI singleton in Program.cs.
//
// This deliberately does NOT live on Core.Rounds' RoundSchedulingOptions
// (see that type's own doc comment) — PuzzleCount is xG-Path-specific
// generation config, not a generic round-scheduling concern every GameKey
// shares, the same reasoning that moved GridSize off RoundSchedulingOptions
// and onto GridGenerationOptions in this same story (S-084).
public class PathGenerationOptions
{
    // Tier 0 has no admin-driven PathTemplate management yet (S-081/S-084) —
    // PathTemplateResolver find-or-creates a template of this puzzle count
    // on demand, same pattern GridTemplateResolver already uses for
    // GridTemplate.Size.
    public int PuzzleCount { get; set; } = 4;
}
