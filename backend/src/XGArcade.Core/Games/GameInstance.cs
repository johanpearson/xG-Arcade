namespace XGArcade.Core.Games;

// The only thing Core.Rounds needs back from IGameModule.GenerateInstanceAsync
// — its Id is what gets stored as Round.GameInstanceId (ADR-0003). Core never
// sees the concrete instance shape (e.g. a GridInstance's cells).
public class GameInstance
{
    public required Guid Id { get; set; }
}
