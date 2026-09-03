using XGArcade.Core.Games;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// COMP-17: IGameModule implementation for xG Connect, a fourth game hosted
// on the platform alongside Games.XGGrid (COMP-05), Games.XGPath (COMP-11),
// and Games.XGPredict (COMP-15) — but structurally unlike all three. Per
// ADR-0103, ConnectMatch is a new first-class concept, never a Round: it is
// created on demand by Core.Social (COMP-16) the instant a challenge is
// accepted or a matchmaking pairing forms (REQ-1402/1403), scoped to exactly
// two named UserIds, and resolves to a win/draw/forfeit outcome (REQ-1409)
// rather than a FinalPoints total. See ADR-0103's "Decision" section for the
// full reasoning and its "For AI agents" section for the constraints this
// class must not violate.
//
// This story (S-211 scaffold only) wires the IGameModule boundary/GameKey
// registration and a real PurgeUserDataAsync — it does NOT implement target-
// pick selection (REQ-1404), chain-step submission (REQ-1406/1407), or
// scoring/resolution (REQ-1408/1409). That business logic belongs in its own
// service(s) layered on top of IConnectMatchRepository (mirroring
// GridGameModule/XGPathGameModule/XGPredictGameModule's own thin-adapter-
// composing-independent-services shape), built by S-211 onward — do not add
// it here without also updating this class's own doc comment and
// docs/architecture-document.md's COMP-17 row.
//
// Every IGameModule method below except PurgeUserDataAsync is a round-
// generation-shaped method that genuinely does not apply to xG Connect's
// pairwise, on-demand match shape (ADR-0103's own "narrower reading of
// IGameModule" paragraph) — they throw NotSupportedException rather than
// NotImplementedException, following GetCellCategoryTypesAsync's existing
// "this game has no such concept, not merely not-yet-built" precedent
// (COMP-11/XGPathGameModule, COMP-15/XGPredictGameModule), not a "TODO, come
// back and implement this" one. Do not wire GenerateInstanceAsync into
// RoundGenerationService/IRoundSchedulingOptionsResolver, do not add
// "xg-connect" to GuessSubmissionAllowedGameKeys, and do not route
// ScoreSubmissionAsync through GuessSubmissionService — a resolved
// challenge/pairing writes ConnectMatch directly (already true as of S-210),
// and target-pick/chain-step submission will get their own dedicated
// endpoints (mirroring PredictEndpoints, not GuessEndpoints), not this
// method.
public class XGConnectGameModule(
    IConnectMatchRepository connectMatchRepository,
    IConnectChatMessageRepository connectChatMessageRepository) : IGameModule
{
    public const string XGConnectGameKey = "xg-connect";

    public string GameKey => XGConnectGameKey;

    public Task<GameInstance?> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Connect has no round-generation concept — ConnectMatch is created directly by Core.Social on " +
            "challenge-accept or matchmaking-pair, never by RoundGenerationService. See ADR-0103.");

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Connect never writes a Guess row and is never reached through GuessSubmissionService — " +
            "target-pick selection (REQ-1404) and chain-step submission (REQ-1406/1407) get their own " +
            "dedicated endpoints, built by S-211 onward. See ADR-0103.");

    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Connect has no 'cell' concept and no Round-scoped unanswered-cell penalty — a ConnectMatch " +
            "resolves to a native win/draw/forfeit outcome (REQ-1409), never Core.Scoring's FinalPoints shape. " +
            "See ADR-0103.");

    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Connect has no per-cell attempt-cap concept — REQ-1407's two-strikes-per-step penalty/bust rule " +
            "is a different, per-chain-step mechanism, owned by S-214's own service logic, not this method. " +
            "See ADR-0103.");

    public Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "xG Connect has no row/col category concept — REQ-215's PlayerSuggestion flow does not apply to " +
            "xg-connect.");

    // REQ-216: xG Connect has no "wrong player name guess on a locked cell"
    // concept to resolve an identity for. Same unconditional-null precedent
    // XGPathGameModule/XGPredictGameModule already established for "not
    // applicable to this game" — GuessSubmissionService never calls this for
    // "xg-connect" anyway (it is unreachable, since this GameKey is never in
    // GuessSubmissionAllowedGameKeys), but returning null rather than
    // throwing keeps this method consistent with every other game module's
    // implementation of the same signature.
    public Task<WrongGuessPlayerInfo?> ResolveWrongGuessPlayerAsync(
        Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WrongGuessPlayerInfo?>(null);

    // REQ-710/ADR-0101: xG Connect's four per-user tables — ConnectMatch
    // (PlayerAUserId/PlayerBUserId), ConnectTargetPick.UserId,
    // ConnectChainStep.UserId, and (S-215) ConnectChatMessage.SenderUserId —
    // are this module's OWN persistence (COMP-17), so AccountDeletionService
    // (Core.Auth) never reaches them directly; it
    // calls this method through IGameModule instead (see that interface's
    // own doc comment). All four anonymize in place rather than hard-delete
    // (every one of those columns is nullable with no FK to User, per each
    // entity's own doc comment) — the other participant's match/chain
    // history depends on these rows surviving, same reasoning as
    // Guess/PredictMatchPrediction's own REQ-710 treatment. This is the one
    // place in the codebase allowed to reference IConnectMatchRepository
    // directly from outside Games.XGConnect's own boundary, because this
    // class IS Games.XGConnect/COMP-17, not Core.
    //
    // S-215/REQ-1410: also anonymizes ConnectChatMessage.SenderUserId via
    // the separate IConnectChatMessageRepository — chat messages are
    // COMP-17's own persistence too (a fourth per-user table alongside the
    // three IConnectMatchRepository.AnonymizeUserDataAsync already covers),
    // just owned by a different repository (see that interface's own doc
    // comment for why chat has its own repository). Both calls run
    // independently; there is no ordering dependency between them.
    public async Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await connectMatchRepository.AnonymizeUserDataAsync(userId, cancellationToken);
        await connectChatMessageRepository.AnonymizeSenderAsync(userId, cancellationToken);
    }
}
