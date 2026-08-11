using XGArcade.Core.Games;
using XGArcade.Data.Entities;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of GridGameModule —
// owns REQ-207/208/209's name-resolution work (exact/alias/fuzzy matching,
// category-fit filtering, disambiguation) plus REQ-216's wrong-guess
// identity resolution.
public interface IGridNameMatcher
{
    // REQ-208's three-stage matching order — exact primary name, then
    // alias, then bounded fuzzy — each stage only runs if the previous one
    // resolved to zero candidates satisfying both of the cell's categories.
    // instanceId is used only for the REQ-209 disambiguation log line, not
    // for any lookup — see the implementation's own doc comment.
    Task<ScoreResult> FindMatchAsync(
        GridCell cell, string normalizedName, Guid? chosenPlayerId, Guid instanceId, CancellationToken cancellationToken);

    // REQ-216/ADR-0057: resolves the guessed player's canonical name (and,
    // independently, an optional photo) for a submitted name — see
    // IGameModule.ResolveWrongGuessPlayerAsync's own doc comment for the
    // full "when/how often" contract the caller enforces. Unlike
    // IGameModule's own method, this deliberately has no instanceId
    // parameter — the original implementation never referenced it (it only
    // existed to satisfy IGameModule's shape), so it isn't carried into this
    // narrower interface; GridGameModule's adapter keeps instanceId in its
    // own signature (required by IGameModule) but doesn't forward it here.
    Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(string submittedName, CancellationToken cancellationToken);
}
