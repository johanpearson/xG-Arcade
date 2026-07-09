namespace XGArcade.Games.XGGrid;

// REQ-101's configurable thresholds. Registered with these defaults via DI;
// tests construct GridGameModule with tighter values directly so retry/abort
// branches don't need hundreds of iterations to exercise.
public class GridGenerationOptions
{
    public int MinValidAnswers { get; set; } = 3;
    public int MaxAttempts { get; set; } = 500;
}
