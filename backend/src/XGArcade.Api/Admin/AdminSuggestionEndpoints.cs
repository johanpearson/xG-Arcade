using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Api.Admin;

// REQ-509/REQ-510 (S-090): admin review of a submitted player suggestion
// (REQ-215/S-089) plus the standalone manual search-and-add path — a new
// file, deliberately separate from AdminEndpoints.cs (scoped to REQ-501/
// 502/503 by that file's own header comment) and never folded into that
// file's /admin/player-data/unverified queue (ADR-0053 is explicit and
// non-negotiable about this: PlayerSuggestion never becomes a row type in
// that queue).
//
// ADR-0053's other half — REQ-510's standalone path is "a variant entry
// point," not a third view — is why this file has exactly ONE fetch helper
// (LookupPlayerAsync) and exactly ONE commit helper (CommitPlayerDataAsync),
// each called from two endpoints (suggestion-scoped and standalone) rather
// than duplicated. Every write here goes through IPlayerStoreRepository's
// existing PlayerOverride/PlayerAttribute mechanism (COMP-06) — never
// PlayerNameIndex, under any circumstance (ADR-0007/ADR-0053).
//
// Write-path design (flagged for a possible ADR during the docs phase, per
// this story's own task description — this is a genuinely new structural
// choice, not dictated by an existing REQ/ADR): nationality is single-valued
// per player, so it's written via PlayerOverride (Field="nationality"),
// exactly like REQ-501's manual-override path — its Reason/LockedByAdminId/
// LockedAt columns directly satisfy REQ-509's "a reason recorded, audit
// fields set" criterion for this field. Club(s) are genuinely multi-valued
// (REQ-113's "ever played for, at any career point"), so each confirmed club
// not already an effective attribute for the player is added as its own new
// PlayerAttribute row (AttributeType="club") instead — additive, and
// correctly supports more than one true club, unlike PlayerOverride's
// "replaces every cached attribute of that type" semantics
// (PlayerStoreRepository.HasEffectiveAttributeAsync). PlayerAttribute has no
// audit columns, so REQ-509's "logged with admin_id and a timestamp"
// requirement is satisfied by PlayerSuggestion.ResolvedByAdminId/ResolvedAt
// for the suggestion-scoped commit/reject path (set by
// IPlayerSuggestionRepository.ResolveAsync), and by a structured ILogger
// line for REQ-510's standalone path (which has no suggestion row to attach
// that to) — matching how REQ-503's existing remove action already logs
// instead of adding a new audit-log table.
public static class AdminSuggestionEndpoints
{
    public static void MapAdminSuggestionEndpoints(this WebApplication app)
    {
        // REQ-509: "every pending suggestion is listed with the player name,
        // the asserted club(s), the asserted nationality, the submitting
        // user, and the submission timestamp." Resolves every row's
        // submitting user's display name in one batched query
        // (IUserRepository.GetByIdsAsync), same "no N+1 loop" discipline
        // AdminEndpoints.GetUnverifiedPlayerData already established for the
        // identical reason. SubmittingUserId has no FK (PlayerSuggestion's
        // own doc comment) — a user hard-deleted since submission (REQ-710)
        // simply resolves to a null display name, not an error.
        app.MapGet("/admin/suggestions", async (
            IPlayerSuggestionRepository playerSuggestionRepository,
            IUserRepository userRepository,
            CancellationToken cancellationToken) =>
        {
            var pending = await playerSuggestionRepository.GetPendingAsync(cancellationToken);

            var userIds = pending.Select(s => s.SubmittingUserId).Distinct().ToList();
            var users = await userRepository.GetByIdsAsync(userIds, cancellationToken);
            var displayNameByUserId = users.ToDictionary(u => u.Id, u => u.DisplayName);

            var responses = pending
                .Select(s => new PendingSuggestionResponse(
                    s.Id,
                    s.PlayerName,
                    s.AssertedClubs.Select(c => c.ClubName).ToList(),
                    s.AssertedNationality,
                    s.SubmittingUserId,
                    displayNameByUserId.GetValueOrDefault(s.SubmittingUserId),
                    s.RowCategoryType,
                    s.ColCategoryType,
                    s.CreatedAt))
                .ToList();

            return Results.Ok(responses);
        }).RequireAuthorization("Admin");

        // REQ-509: "the system runs the same Wikidata SPARQL query shape
        // already used for player-attribute resolution... to fetch every
        // club the player has ever been recorded as a member of and the
        // player's nationality" — for this specific suggestion's own
        // PlayerName, never a name the caller supplies (the suggestion
        // itself, not the request body, is the source of truth for which
        // name gets looked up).
        app.MapPost("/admin/suggestions/{id:guid}/lookup", async (
            Guid id,
            IPlayerSuggestionRepository playerSuggestionRepository,
            IWikidataClient wikidataClient,
            ILogger<AdminSuggestionEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var suggestion = await playerSuggestionRepository.GetByIdAsync(id, cancellationToken);
            if (suggestion is null)
                return Results.NotFound();

            if (suggestion.Status != PlayerSuggestionStatus.Pending)
            {
                return Results.Problem(
                    title: "Suggestion already resolved",
                    detail: "This suggestion has already been committed or rejected.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            WikidataPlayerLookupResponse response;
            try
            {
                response = await LookupPlayerAsync(suggestion.PlayerName, wikidataClient, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                // REQ-509/ADR-0046: a lookup that fails/times out is reported
                // as "lookup unavailable, try again" — never silently treated
                // as no-match. Same 503 shape GuessEndpoints.cs already uses
                // for GuessSubmissionOutcome.LiveLookupUnavailable. The
                // exception's own Message is deliberately NOT surfaced here
                // (unlike an /internal/* endpoint's CI-log carve-out,
                // docs/coding-guidelines.md) — this endpoint's caller is an
                // admin's browser, not a scheduled job's own log. Logged
                // server-side instead (bug fix, 2026-08-09) so a production
                // "Lookup unavailable" report is diagnosable — before this
                // fix both admin lookup endpoints failed identically and
                // silently, indistinguishable from each other in the logs.
                logger.LogWarning(
                    ex,
                    "Wikidata lookup failed for suggestion {SuggestionId} (player name {PlayerName}) via the suggestion-scoped admin lookup endpoint",
                    id, suggestion.PlayerName);
                return Results.Problem(
                    title: "Live verification unavailable",
                    detail: "We couldn't reach Wikidata to verify this player. Please try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(response);
        }).RequireAuthorization("Admin");

        // REQ-509: "the corresponding PlayerAttribute/PlayerOverride data is
        // written the same way REQ-501's manual-override path writes it
        // today... and the suggestion's own stored state moves to a
        // resolved/committed state — it is never left pending after a
        // commit." The request carries the admin's CONFIRMED values (fetched
        // from Wikidata by the /lookup endpoint above, reviewed by the admin,
        // possibly hand-edited before submission) rather than re-deriving
        // them from a second live lookup — the admin's judgment call is the
        // whole point of this endpoint, not just a rubber stamp on whatever
        // Wikidata happened to return.
        app.MapPost("/admin/suggestions/{id:guid}/commit", async (
            Guid id,
            CommitPlayerDataRequest request,
            ClaimsPrincipal principal,
            IPlayerSuggestionRepository playerSuggestionRepository,
            IPlayerStoreRepository playerStoreRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidateCommitRequest(request);
            if (validationError is not null)
                return validationError;

            var suggestion = await playerSuggestionRepository.GetByIdAsync(id, cancellationToken);
            if (suggestion is null)
                return Results.NotFound();

            if (suggestion.Status != PlayerSuggestionStatus.Pending)
            {
                return Results.Problem(
                    title: "Suggestion already resolved",
                    detail: "This suggestion has already been committed or rejected.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var resolvedAt = timeProvider.GetUtcNow().UtcDateTime;

            var result = await CommitPlayerDataAsync(request, adminId, resolvedAt, playerStoreRepository, cancellationToken);

            // Never pending after this action (REQ-509) — even in the
            // unlikely race where another admin resolved the same suggestion
            // between the GetByIdAsync check above and this call, the
            // PlayerAttribute/PlayerOverride write above has already
            // happened and is not rolled back; ResolveAsync's own false
            // return here just means this row's own Status/ResolvedBy
            // fields weren't the ones to record it first.
            await playerSuggestionRepository.ResolveAsync(id, PlayerSuggestionStatus.Committed, adminId, resolvedAt, cancellationToken);

            return Results.Ok(result);
        }).RequireAuthorization("Admin");

        // REQ-509: "given the fetched Wikidata data does not confirm the
        // suggestion's claim... no PlayerAttribute/PlayerOverride/
        // PlayerNameIndex write occurs, the suggestion's state moves to
        // rejected, and the rejection is logged with admin_id and a
        // timestamp exactly as a commit is." No request body — same "no
        // reason field" simplicity precedent as REQ-503's approve action
        // (AdminEndpoints.cs).
        app.MapPost("/admin/suggestions/{id:guid}/reject", async (
            Guid id,
            ClaimsPrincipal principal,
            IPlayerSuggestionRepository playerSuggestionRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var suggestion = await playerSuggestionRepository.GetByIdAsync(id, cancellationToken);
            if (suggestion is null)
                return Results.NotFound();

            if (suggestion.Status != PlayerSuggestionStatus.Pending)
            {
                return Results.Problem(
                    title: "Suggestion already resolved",
                    detail: "This suggestion has already been committed or rejected.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var rejectedAt = timeProvider.GetUtcNow().UtcDateTime;

            var resolved = await playerSuggestionRepository.ResolveAsync(id, PlayerSuggestionStatus.Rejected, adminId, rejectedAt, cancellationToken);
            if (!resolved)
            {
                // Race window between the Pending check above and this call
                // (another admin resolved it first) — same "already
                // resolved" outcome as the check above, reported the same way.
                return Results.Problem(
                    title: "Suggestion already resolved",
                    detail: "This suggestion has already been committed or rejected.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.NoContent();
        }).RequireAuthorization("Admin");

        // REQ-510: "the admin searches by player name directly, with no
        // pending suggestion involved... the system runs the identical live
        // Wikidata fetch REQ-509 uses." No suggestion row is read, created,
        // or touched by this endpoint at all.
        app.MapPost("/admin/player-search/lookup", async (
            PlayerSearchLookupRequest request,
            IWikidataClient wikidataClient,
            ILogger<AdminSuggestionEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlayerName))
            {
                return Results.Problem(
                    title: "A player name is required",
                    detail: "playerName must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            WikidataPlayerLookupResponse response;
            try
            {
                response = await LookupPlayerAsync(request.PlayerName, wikidataClient, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                // Same ADR-0046 timeout-vs-no-match distinction as
                // /admin/suggestions/{id}/lookup above, and the same
                // server-side-only logging (bug fix, 2026-08-09) — see that
                // endpoint's catch block for the full reasoning.
                logger.LogWarning(
                    ex,
                    "Wikidata lookup failed for player name {PlayerName} via the standalone admin player-search lookup endpoint",
                    request.PlayerName);
                return Results.Problem(
                    title: "Live verification unavailable",
                    detail: "We couldn't reach Wikidata to verify this player. Please try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(response);
        }).RequireAuthorization("Admin");

        // REQ-510: "it is written through the identical commit path as
        // REQ-509's... and this action requires no suggestion record to
        // exist before, during, or after it." Audit trail is a structured
        // ILogger line (admin_id/timestamp/WikidataQid), the same "no
        // audit-log table" precedent REQ-503's existing remove action uses
        // — see this file's own header comment for why the suggestion-scoped
        // commit above doesn't need this (it has PlayerSuggestion.
        // ResolvedByAdminId/ResolvedAt to record it on instead).
        app.MapPost("/admin/player-search/commit", async (
            CommitPlayerDataRequest request,
            ClaimsPrincipal principal,
            IPlayerStoreRepository playerStoreRepository,
            ILogger<AdminSuggestionEndpointsLogCategory> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidateCommitRequest(request);
            if (validationError is not null)
                return validationError;

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var committedAt = timeProvider.GetUtcNow().UtcDateTime;

            var result = await CommitPlayerDataAsync(request, adminId, committedAt, playerStoreRepository, cancellationToken);

            logger.LogInformation(
                "Admin {AdminId} committed player data for WikidataQid {WikidataQid} (Player {PlayerId}) via standalone search at {CommittedAt}",
                adminId, request.WikidataQid, result.PlayerId, committedAt);

            return Results.Ok(result);
        }).RequireAuthorization("Admin");
    }

    // Shared by both /lookup endpoints above — REQ-509/510's identical fetch
    // step, per ADR-0053 ("a variant entry point... not a parallel
    // reimplementation"). Found=false (with every other field null/empty) is
    // a normal, valid outcome (no footballer matches this name on Wikidata),
    // never surfaced as an error — a WikidataQueryException propagates past
    // this method uncaught, for each caller's own catch block to translate
    // to a 503.
    private static async Task<WikidataPlayerLookupResponse> LookupPlayerAsync(
        string playerName, IWikidataClient wikidataClient, CancellationToken cancellationToken)
    {
        var result = await wikidataClient.QueryPlayerCareerAndNationalityByNameAsync(playerName, cancellationToken);
        if (result is null)
            return new WikidataPlayerLookupResponse(false, null, null, null, []);

        return new WikidataPlayerLookupResponse(
            true,
            result.WikidataQid,
            result.FullName,
            result.Nationality,
            result.Clubs.ToList());
    }

    // Shared by both /commit endpoints above — REQ-509/510's identical write
    // step. Resolves (or creates, if this is the first time this WikidataQid
    // has ever been seen) the local Player row, then writes nationality via
    // PlayerOverride (upsert: update in place if one already exists for this
    // player, matching REQ-501's write path/semantics without REQ-501's own
    // raw-CRUD-endpoint 409-then-PUT two-step, which doesn't fit this
    // one-shot review-and-commit UX) and each confirmed club not already an
    // effective attribute for the player as a new PlayerAttribute row — see
    // this file's own header comment for the full write-path reasoning.
    private static async Task<CommitPlayerDataResponse> CommitPlayerDataAsync(
        CommitPlayerDataRequest request,
        Guid adminId,
        DateTime committedAt,
        IPlayerStoreRepository playerStoreRepository,
        CancellationToken cancellationToken)
    {
        var playersByQid = await playerStoreRepository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest(request.WikidataQid, request.FullName, PhotoUrl: null)], cancellationToken);
        var player = playersByQid[request.WikidataQid];

        string? nationality = null;
        if (!string.IsNullOrWhiteSpace(request.Nationality))
        {
            var existingOverride = await playerStoreRepository.GetOverrideAsync(player.Id, "nationality", cancellationToken);
            if (existingOverride is not null)
            {
                existingOverride.Value = request.Nationality;
                existingOverride.Reason = request.Reason;
                existingOverride.LockedByAdminId = adminId;
                existingOverride.LockedAt = committedAt;
                await playerStoreRepository.UpdateOverrideAsync(existingOverride, cancellationToken);
            }
            else
            {
                await playerStoreRepository.AddOverrideAsync(new PlayerOverride
                {
                    Id = Guid.NewGuid(),
                    PlayerId = player.Id,
                    Field = "nationality",
                    Value = request.Nationality,
                    Reason = request.Reason,
                    LockedByAdminId = adminId,
                    LockedAt = committedAt,
                }, cancellationToken);
            }

            nationality = request.Nationality;
        }

        var confirmedClubs = (request.Clubs ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct()
            .ToList();

        var newAttributes = new List<PlayerAttribute>();
        foreach (var club in confirmedClubs)
        {
            var alreadyEffective = await playerStoreRepository.HasEffectiveAttributeAsync(player.Id, "club", club, cancellationToken);
            if (!alreadyEffective)
                newAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club });
        }

        await playerStoreRepository.AddPlayerAttributesBatchAsync(newAttributes, cancellationToken);

        return new CommitPlayerDataResponse(player.Id, nationality, confirmedClubs);
    }

    // Shared validation for both /commit endpoints above.
    private static IResult? ValidateCommitRequest(CommitPlayerDataRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WikidataQid) || string.IsNullOrWhiteSpace(request.FullName))
        {
            return Results.Problem(
                title: "Invalid commit",
                detail: "wikidataQid and fullName are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var hasNationality = !string.IsNullOrWhiteSpace(request.Nationality);
        var hasClubs = request.Clubs is { Count: > 0 } && request.Clubs.Any(c => !string.IsNullOrWhiteSpace(c));
        if (!hasNationality && !hasClubs)
        {
            return Results.Problem(
                title: "Invalid commit",
                detail: "At least one of nationality or clubs must be provided.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Reason is only persisted when a nationality is committed (written to
        // PlayerOverride.Reason for REQ-501 audit purposes) - PlayerAttribute has
        // no audit columns, so requiring it for a clubs-only commit would validate
        // input that's discarded immediately after. See ADR-0060.
        if (hasNationality && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.Problem(
                title: "Invalid commit",
                detail: "reason is required when committing a nationality.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }
}

// Pure log-category marker for ILogger<T> — same pattern as
// AdminEndpointsLogCategory/SuggestionEndpointsLogCategory.
internal sealed class AdminSuggestionEndpointsLogCategory;

public record PendingSuggestionResponse(
    Guid Id,
    string PlayerName,
    IReadOnlyList<string> AssertedClubs,
    string AssertedNationality,
    Guid SubmittingUserId,
    string? SubmittingUserDisplayName,
    string RowCategoryType,
    string ColCategoryType,
    DateTime CreatedAt);

// Found=false means "Wikidata has no footballer matching this name" — a
// normal, valid outcome, never an error (a lookup FAILURE is a 503, not this
// shape with Found=false). Every other field is null/empty exactly when
// Found is false.
public record WikidataPlayerLookupResponse(
    bool Found,
    string? WikidataQid,
    string? FullName,
    string? Nationality,
    IReadOnlyList<string> Clubs);

// The admin's CONFIRMED values for a commit — typically pre-filled from a
// prior /lookup response and reviewed as-is, but not required to be
// identical to it (an admin may hand-correct a value before committing).
// Nationality null/blank means "don't touch this player's nationality
// override"; Clubs empty means "don't add any new club attributes" — but
// ValidateCommitRequest requires at least one of the two to be present.
public record CommitPlayerDataRequest(
    string WikidataQid,
    string FullName,
    string? Nationality,
    IReadOnlyList<string> Clubs,
    string Reason);

// Clubs here is exactly the confirmedClubs list CommitPlayerDataAsync
// computed (trimmed/deduped/non-blank) — not necessarily identical to
// CommitPlayerDataRequest.Clubs, and says nothing about which of them were
// already-effective (skipped) vs. newly written; the caller only needs "what
// ended up confirmed," not a per-club new/skipped breakdown.
public record CommitPlayerDataResponse(Guid PlayerId, string? Nationality, IReadOnlyList<string> Clubs);

public record PlayerSearchLookupRequest(string PlayerName);
