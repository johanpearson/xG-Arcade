using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGConnect;

// ADR-0107: shared candidate-resolution logic for ConnectTargetPickService
// and ConnectChainStepService — both used to independently resolve a
// client-supplied player NAME to a real Player.Id via
// IPlayerRepository.GetPlayersByNormalizedFullNameAsync, deterministically
// picking the lowest Id on a same-name collision (a documented, deliberate
// simplification in both services' own prior comments). A real, reported
// incident showed that simplification is a genuine bug, not just a
// theoretical edge case: two different real footballers both named "Jonas
// Olsson" (different Wikidata QIDs) both got indexed into PlayerNameIndex
// from a routine nationality-pool sweep, and name-only resolution had no
// way to tell them apart — every attempt to connect through the real one
// the player meant was silently validated against the wrong one instead.
//
// Fixed by preferring an unambiguous WikidataQid when the caller can supply
// one (now that PlayerNameIndex carries it — see that entity's own doc
// comment) — GetOrCreatePlayersByWikidataQidAsync resolves a real person's
// Player row exactly, get-or-create so a player who's been indexed but
// never before referenced by any game module still resolves cleanly rather
// than 404ing. Name-only resolution is kept as a fallback for a caller (an
// older frontend build, or a suggestion indexed before WikidataQid existed
// on it) that genuinely cannot supply one yet — not a permanent parallel
// path, a transition one; see ADR-0107's own Consequences for when to
// revisit dropping it.
//
// A candidateWikidataQid that fails WikidataQid.IsValid is treated the same
// as none supplied (falls back to name resolution) rather than erroring —
// never trust a client-supplied string's shape blindly, but a malformed
// value here still has a working fallback, so there's no reason to hard-fail
// the whole submission over it.
internal static class ConnectCandidateResolver
{
    internal enum Outcome
    {
        Resolved,
        NotFound,
    }

    internal readonly record struct Result(Outcome Outcome, Guid PlayerId = default);

    internal static async Task<Result> ResolveAsync(
        IPlayerRepository playerRepository,
        string candidateName,
        string? candidateWikidataQid,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(candidateWikidataQid) && WikidataQid.IsValid(candidateWikidataQid))
        {
            var creationResults = await playerRepository.GetOrCreatePlayersByWikidataQidAsync(
                [new PlayerCreationRequest(candidateWikidataQid, candidateName, PhotoUrl: null)], cancellationToken);

            // Always populated for a valid, well-formed QID — GetOrCreatePlayersByWikidataQidAsync
            // creates a row rather than reporting "not found" for one it doesn't already
            // have. Defensive fallback only, not an expected branch in practice.
            return creationResults.TryGetValue(candidateWikidataQid, out var creationResult)
                ? new Result(Outcome.Resolved, creationResult.Player.Id)
                : new Result(Outcome.NotFound);
        }

        var normalizedName = PlayerNameNormalizer.Normalize(candidateName);
        var candidates = await playerRepository.GetPlayersByNormalizedFullNameAsync(normalizedName, cancellationToken);
        if (candidates.Count == 0)
            return new Result(Outcome.NotFound);

        // The exact same-name-collision fallback this resolver exists to make
        // unnecessary whenever a WikidataQid is available — kept only for the
        // transition window described in this class's own doc comment above.
        return new Result(Outcome.Resolved, candidates.OrderBy(p => p.Id).First().Id);
    }
}
