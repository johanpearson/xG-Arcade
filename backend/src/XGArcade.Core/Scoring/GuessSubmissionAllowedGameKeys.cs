namespace XGArcade.Core.Scoring;

// S-200/ADR-0098 Consequences: the explicit allow-list of GameKeys
// GuessSubmissionService is permitted to process, built by the composition
// root from each Guess-based game's own GameKey constant (ADR-0003 — Core
// never hardcodes a GameKey string literal or references a specific game
// module type). This is an allow-list ("xg-grid"/"xg-path" today), not a
// deny-list naming "xg-predict" — a future third non-Guess-based game is
// rejected the same way without any further change to
// GuessSubmissionService itself.
//
// A dedicated type rather than a raw IReadOnlyCollection<string> constructor
// parameter, so this DI registration (AddSingleton) can never collide with
// some other component's future need for a plain string collection —
// same reasoning RoundSchedulingOptions already established for per-GameKey
// config in this codebase.
public class GuessSubmissionAllowedGameKeys
{
    public required IReadOnlyCollection<string> GameKeys { get; init; }
}
