using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Games;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGPath;

namespace XGArcade.Api.Path;

// REQ-1203/S-082: the client-facing read path for the active xg-path
// round's puzzles and their progressively-revealed clues — mirrors
// XGArcade.Api.Rounds.RoundEndpoints's GET /rounds/current shape/pattern
// closely (same auth handling via ClaimsPrincipal/IUserRepository, same
// roundRepository.GetActiveByGameKeyAsync call, same only-the-requesting-
// player's-own-guess-state contract). Nothing here writes anything —
// POST /rounds/{roundId}/cells/{cellId}/guesses
// (XGArcade.Api.Guesses.GuessEndpoints) is already game-agnostic
// (routes through IGuessSubmissionService/IGameModuleResolver by
// round.GameKey) and remains the only write path for xg-path guesses too,
// once XGPathGameModule.ScoreSubmissionAsync/GetMaxAttemptsForCellAsync
// are implemented (S-082) — no second guess-submission endpoint here.
//
// ADR-0016/ADR-0048: this read (PathInstance/PathPuzzle, via
// IPathInstanceRepository, direct from the Api layer) is ADR-0016's
// direct-repository-read pattern applied to a second game module —
// resolved, not an open question: ADR-0048 compared this shape against
// GridInstance/GridCell (RoundEndpoints' own GET /rounds/current) and
// confirmed the per-game direct-repository-read pattern as the accepted
// long-term shape, rather than designing a generalized IGameModule read
// method from only two real data points. See ADR-0048 for the reasoning.
public static class PathEndpoints
{
    // REQ-1203's nationality clue reads PlayerAttribute's "nationality"
    // rows — same AttributeType vocabulary GridGameModule already uses for
    // country/nationality — never PlayerOverride/HasEffectiveAttributeAsync
    // (that precedence logic exists for xG Grid's correctness *checking*;
    // this is a display-only read of whatever is cached, same as every
    // other clue source here).
    private const string NationalityAttributeType = "nationality";

    public static void MapPathEndpoints(this WebApplication app)
    {
        app.MapGet("/path/current", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IPathInstanceRepository pathInstanceRepository,
            IGuessRepository guessRepository,
            IPlayerStoreRepository playerStoreRepository,
            IGameModuleResolver gameModuleResolver,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var round = await roundRepository.GetActiveByGameKeyAsync(XGPathGameModule.XGPathGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // ADR-0041: resolved so GetMaxAttemptsForCellAsync can be called
            // per puzzle, same precedent as RoundEndpoints.
            var gameModule = gameModuleResolver.Resolve(round.GameKey);

            // Reads PathInstance/PathPuzzle directly, bypassing IGameModule
            // — see this file's own ADR-0016 scope note above.
            var instance = await pathInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references PathInstance '{round.GameInstanceId}' which does not exist.");

            var guesses = await guessRepository.GetByRoundAndUserAsync(round.Id, user.Id, cancellationToken);
            var guessByPuzzleId = guesses.ToDictionary(g => g.CellId);

            // Bulk fetch, once for the whole instance — same "one query for
            // the batch, never one per cell" discipline RoundEndpoints
            // already follows for its own player lookups.
            var targetPlayerIds = instance.Puzzles.Select(p => p.TargetPlayerId).Distinct().ToList();
            var stintsByPlayerId = await playerStoreRepository.GetCareerStintsByPlayerIdsAsync(targetPlayerIds, cancellationToken);
            var playersById = await playerStoreRepository.GetPlayersByIdsAsync(targetPlayerIds, cancellationToken);
            var attributesByPlayerId = await playerStoreRepository.GetPlayerAttributesByPlayerIdsAsync(targetPlayerIds, cancellationToken);

            // ADR-0041: a plain foreach, not a LINQ .Select(...) —
            // GetMaxAttemptsForCellAsync must be awaited per puzzle, same
            // reasoning RoundEndpoints' own comment gives.
            var puzzles = new List<CurrentPathPuzzleResponse>(instance.Puzzles.Count);
            foreach (var puzzle in instance.Puzzles)
            {
                guessByPuzzleId.TryGetValue(puzzle.Id, out var guess);
                var attemptCount = guess?.AttemptCount ?? 0;
                var isCorrect = guess?.IsCorrect ?? false;

                var maxAttemptsForPuzzle = await gameModule.GetMaxAttemptsForCellAsync(round.GameInstanceId, puzzle.Id, cancellationToken);
                var locked = isCorrect || attemptCount >= maxAttemptsForPuzzle;

                var revealedTurnCount = PathClueSequenceBuilder.GetRevealedTurnCount(attemptCount, isCorrect);

                // Bug fix (2026-08-08, REQ-1203): filter out any leftover
                // pre-2026-08-02 youth/age-grade national-team row before
                // this player's stints ever reach PathClueSequenceBuilder —
                // see PathCareerStintFilter's own doc comment for the full
                // "why a read-time filter, not a cleanup script" reasoning.
                var stints = stintsByPlayerId.TryGetValue(puzzle.TargetPlayerId, out var playerStints)
                    ? PathCareerStintFilter.ExcludeYouthNationalTeams(playerStints).OrderBy(s => s.SequenceOrder).ToList()
                    : [];
                playersById.TryGetValue(puzzle.TargetPlayerId, out var targetPlayer);
                var nationality = attributesByPlayerId.TryGetValue(puzzle.TargetPlayerId, out var attributes)
                    ? attributes.FirstOrDefault(a => a.AttributeType == NationalityAttributeType)?.AttributeValue
                    : null;

                var allTurns = PathClueSequenceBuilder.BuildSequence(
                    stints, targetPlayer?.Position, nationality, targetPlayer?.BirthYear);

                // REQ-1204/"never leak the answer for a puzzle the player
                // can still guess on": only ever the clue turns themselves
                // are sent here — target player identity never appears
                // anywhere in this response while the puzzle is still live,
                // only once it's `locked` (solved OR attempt cap exhausted,
                // see the guessResponse block below).
                var revealedTurns = allTurns.Take(revealedTurnCount).Select(ToTurnResponse).ToList();

                CurrentPathGuessResponse? guessResponse = null;
                if (guess is not null)
                {
                    // UX fix (bug-bundle fix, 2026-08-02, reported via user
                    // testing): gated on `locked`, not `isCorrect` — a
                    // puzzle also locks when the attempt cap is exhausted
                    // without a correct guess (see `locked`'s own assignment
                    // above), and a player in that state was previously
                    // never told who the target player actually was, with no
                    // way to find out. The boundary that must never be
                    // crossed is "never leak the answer for a puzzle the
                    // player can still guess on" — once the puzzle is
                    // locked, for EITHER reason, revealing the answer is
                    // exactly the point, not a leak. (The old comment here
                    // said "never leak the answer for an unsolved puzzle" —
                    // that phrasing conflated "unsolved" with "still live,"
                    // which stopped being the same thing the moment an
                    // exhausted-attempts puzzle needed its answer revealed
                    // too.)
                    var resolvedName = locked ? targetPlayer?.FullName : null;
                    var resolvedPhotoUrl = locked ? targetPlayer?.PhotoUrl : null;

                    guessResponse = new CurrentPathGuessResponse(
                        isCorrect, attemptCount, locked, guess.SubmittedName, resolvedName, resolvedPhotoUrl);
                }

                puzzles.Add(new CurrentPathPuzzleResponse(puzzle.Id, revealedTurns, guessResponse));
            }

            return Results.Ok(new CurrentPathResponse(round.Id, round.StartTime, round.EndTime, round.AllowGuessChange, puzzles));
        }).RequireAuthorization();
    }

    // DTOs at the API boundary (coding-guidelines.md) — PathClueTurn/
    // PathClubClue (XGArcade.Games.XGPath) are never serialized directly;
    // mapped here the same way GuessEndpoints maps Core.Games.
    // DisambiguationCandidate to DisambiguationCandidateResponse.
    private static PathClueTurnResponse ToTurnResponse(PathClueTurn turn) =>
        new(
            turn.TurnNumber,
            turn.Kind.ToString(),
            turn.Clubs?.Select(c => new PathClubClueResponse(c.ClubName, c.AppearanceCount)).ToList(),
            turn.YearRanges,
            turn.TextValue);
}

public record CurrentPathResponse(
    Guid RoundId,
    DateTime StartTime,
    DateTime EndTime,
    bool AllowGuessChange,
    IReadOnlyList<CurrentPathPuzzleResponse> Puzzles);

// Guess is null when the requesting player hasn't attempted this puzzle yet
// — same shape as RoundEndpoints.CurrentRoundCellResponse.Guess, this
// response only ever carries the requesting player's own guess, never
// another player's.
public record CurrentPathPuzzleResponse(
    Guid PuzzleId,
    IReadOnlyList<PathClueTurnResponse> Clues,
    CurrentPathGuessResponse? Guess);

// Kind is serialized as its enum name (string) rather than PathClueKind
// directly — an Api-boundary DTO shouldn't require the frontend to share
// the Games.XGPath assembly's enum type. Exactly one of Clubs/YearRanges/
// TextValue is non-null per turn, selected by Kind — see PathClueTurn's own
// doc comment for which.
public record PathClueTurnResponse(
    int TurnNumber,
    string Kind,
    IReadOnlyList<PathClubClueResponse>? Clubs,
    IReadOnlyList<string>? YearRanges,
    string? TextValue);

public record PathClubClueResponse(string ClubName, int? AppearanceCount);

// Same only-when-IsCorrect rule for ResolvedPlayerName/ResolvedPlayerPhotoUrl
// as RoundEndpoints.CurrentRoundGuessResponse — never a substitute for
// SubmittedName, which is unchanged and still the raw as-typed text.
public record CurrentPathGuessResponse(
    bool IsCorrect, int AttemptCount, bool Locked, string SubmittedName,
    string? ResolvedPlayerName, string? ResolvedPlayerPhotoUrl);
