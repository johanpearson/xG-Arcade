namespace XGArcade.Games.XGGrid;

// REQ-101's configurable thresholds. Registered with these defaults via DI;
// tests construct GridGameModule with tighter values directly so retry/abort
// branches don't need hundreds of iterations to exercise.
public class GridGenerationOptions
{
    public int MinValidAnswers { get; set; } = 5;

    // 500 is effectively "no ceiling" in practice — MaxDuration below is
    // what actually bounds a real run; this stays only as a hard backstop
    // (e.g. against a reference-data bug that makes every candidate reject
    // instantly with zero live-lookup cost, which MaxDuration alone
    // wouldn't catch quickly).
    public int MaxAttempts { get; set; } = 500;

    // ADR-0023: the 2026-07-12/13 dev incident — a real run ran for 4+
    // minutes of sequential live Wikidata calls before Azure's own ingress
    // killed the connection with a 504, well after MaxAttempts's 500-count
    // ceiling could ever have helped. This is what actually bounds
    // PickHeadersAsync's wall-clock time, well under any known
    // infrastructure timeout (Azure Container Apps' dev ingress currently
    // times out at 240s) — generation always aborts cleanly into
    // GridGenerationException on its own terms instead of being cut off
    // mid-flight with no caller-visible response at all.
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(90);
}
