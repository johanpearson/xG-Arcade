namespace XGArcade.Core.Games;

// The only thing Core.Rounds needs back from IGameModule.GenerateInstanceAsync
// — its Id is what gets stored as Round.GameInstanceId (ADR-0003). Core never
// sees the concrete instance shape (e.g. a GridInstance's cells).
public class GameInstance
{
    public required Guid Id { get; set; }

    // ADR-0102: optional, module-supplied override for the generated
    // Round's StartTime/EndTime, used instead of RoundGenerationService's
    // default chain-math formula (`latest?.EndTime ?? now` / `startTime +
    // RoundDuration`) when non-null. Exists because chain-math timing is
    // agnostic to what a game's content actually represents — xG Predict's
    // matches have real-world kickoff times a fixed RoundDuration cannot
    // track (see ADR-0102's worked "skip" example). xg-grid/xg-path leave
    // both null (their content has no independent real-world timing of its
    // own), which is a complete no-op — behavior for those games is
    // unchanged.
    public DateTime? SuggestedStartTime { get; set; }
    public DateTime? SuggestedEndTime { get; set; }
}
