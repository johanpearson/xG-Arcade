namespace XGArcade.Core.Games;

// COMP-03 (Core.Rounds) resolves a Round's GameKey to exactly one
// IGameModule implementation through this — see IGameModule's own doc
// comment and ADR-0003/architecture-document.md boundary rule 2. As of
// S-080, two IGameModule implementations are registered (GridGameModule,
// XGPathGameModule) — this is exactly the "resolve several by GameKey"
// case this interface exists for, and Core.Rounds needed no change to
// support it. Any caller that needs a *specific* game's module (not
// "whichever Round.GameKey says") must resolve by that game's own GameKey
// constant through here too, never take a raw `IGameModule` from DI —
// with more than one registered, that resolves to an unspecified
// implementation.
public interface IGameModuleResolver
{
    IGameModule Resolve(string gameKey);
}

public class GameModuleResolver(IEnumerable<IGameModule> gameModules) : IGameModuleResolver
{
    public IGameModule Resolve(string gameKey) =>
        gameModules.FirstOrDefault(m => m.GameKey == gameKey)
            ?? throw new InvalidOperationException($"No IGameModule registered for GameKey '{gameKey}'.");
}
