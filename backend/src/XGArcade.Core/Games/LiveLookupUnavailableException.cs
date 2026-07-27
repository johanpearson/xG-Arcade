namespace XGArcade.Core.Games;

// REQ-211 (2026-07-27 fix): thrown by an IGameModule.ScoreSubmissionAsync
// implementation when its own live-lookup fallback could not complete in
// time to answer a guess — a genuine "we don't know yet," never a "here's a
// scored incorrect guess." Defined here, in Core.Games, rather than in the
// throwing game module's own assembly (e.g. XGArcade.Games.XGGrid) so both
// halves of the contract — IGameModule's implicit "may throw this" and
// GuessSubmissionService's catch — depend only on a type Core already owns,
// never on a game-specific or DataSync-specific type (ADR-0003: Core must
// never reference Wikidata/GridGameModule directly). This is structurally
// the same shape as GuessScoringException/GridGenerationException
// (XGArcade.Games.XGGrid) signaling a game-module-specific failure upward —
// the one difference is this one is caught INSIDE Core
// (GuessSubmissionService), not passed through uncaught to the API layer,
// because Core needs to turn it into a specific, non-attempt-consuming
// GuessSubmissionOutcome (LiveLookupUnavailable) rather than a bare 5xx.
//
// The owning game module (GridGameModule for xg-grid) is responsible for
// catching whatever underlying exception its own live-lookup dependency
// throws (e.g. XGArcade.DataSync.Wikidata.WikidataQueryException) and
// translating it into this type before it crosses back into
// GuessSubmissionService — Core itself must never catch or reference the
// underlying game/DataSync-specific exception type.
//
// Structural decision flagged for a dedicated ADR in the docs pass — this is
// a new cross-boundary signal between a game module and Core, not a pure
// bug fix.
public class LiveLookupUnavailableException(string message) : Exception(message);
