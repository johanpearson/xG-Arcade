namespace XGArcade.Core.Games;

// COMP-03 (Core.Rounds) resolves a Round's GameKey to exactly one
// IGameModule implementation through this — see IGameModule's own doc
// comment and ADR-0003/architecture-document.md boundary rule 2. Only one
// IGameModule is registered today (GridGameModule); resolving several by
// GameKey is what this exists to make trivial once a second game module
// is added, without Core.Rounds ever needing to change.
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
