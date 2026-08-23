using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Api.Admin;

// S-012 (docs/backlog.md): minimal admin endpoints for REQ-501/502/503 —
// PlayerOverride CRUD and the unverified-PlayerData review list. Every
// endpoint here requires the "Admin" policy (AdminAuthorizationHandler,
// Admin__UserIds-based, see architecture-document.md).
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // REQ-502/503: the review view's candidate list, with source and
        // confidence visible for each row. Resolves every row's player name
        // in one batched query (GetPlayersByIdsAsync) rather than one
        // GetPlayerByIdAsync call per row — the original per-row loop was
        // fine against S-012's tiny test fixtures but became an N+1 query
        // storm once real Wikidata-sync volume built up (thousands of
        // unverified rows), which is what made this endpoint hang once
        // S-026 gave it a real UI caller. Same bulk-lookup shape
        // RoundEndpoints.cs already uses for the identical reason.
        app.MapGet("/admin/player-data/unverified", async (
            // S-106 (pure refactor): both calls this endpoint makes moved
            // out of IPlayerStoreRepository.
            IPlayerDataRepository playerDataRepository,
            IPlayerRepository playerRepository,
            CancellationToken cancellationToken) =>
        {
            var unverified = await playerDataRepository.GetUnverifiedPlayerDataAsync(cancellationToken);

            var playerIds = unverified.Select(data => data.PlayerId).Distinct().ToList();
            var playersById = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

            var responses = unverified
                .Select(data => new UnverifiedPlayerDataResponse(
                    data.Id, data.PlayerId,
                    playersById.TryGetValue(data.PlayerId, out var player) ? player.FullName : string.Empty,
                    data.Field, data.Value, data.Source, data.Confidence, data.SyncedAt))
                .ToList();

            return Results.Ok(responses);
        }).RequireAuthorization("Admin");

        // REQ-503 (2026-07-20 extension): the "approve" action. Bulk
        // includes single-row as the N=1 case, matching the review view's
        // "select one row" and "select all" UI needs with one endpoint. No
        // `reason` field — unlike POST /admin/player-overrides (REQ-501)
        // below, approve is deliberately simpler and doesn't require one.
        // A row that no longer exists or is no longer "unverified" (deleted
        // or changed by another admin between selection and submission)
        // fails independently of the rest of the batch — this always
        // returns 200 with a per-id result so the caller can show which
        // rows succeeded and which failed, never an all-or-nothing
        // success/failure for the whole batch.
        app.MapPost("/admin/player-data/approve", async (
            ApprovePlayerDataRequest request,
            ClaimsPrincipal principal,
            // S-106 (pure refactor): ApprovePlayerDataAsync moved to
            // IPlayerDataRepository.
            IPlayerDataRepository playerDataRepository,
            CancellationToken cancellationToken) =>
        {
            if (request.PlayerDataIds is null || request.PlayerDataIds.Count == 0)
            {
                return Results.Problem(
                    title: "Invalid approval request",
                    detail: "playerDataIds must contain at least one id.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var outcomes = await playerDataRepository.ApprovePlayerDataAsync(request.PlayerDataIds, adminId, cancellationToken);

            var results = outcomes
                .Select(o => new PlayerDataApprovalResult(o.PlayerDataId, o.Approved, o.FailureReason?.ToString()))
                .ToList();

            return Results.Ok(new ApprovePlayerDataResponse(results));
        }).RequireAuthorization("Admin");

        // REQ-503 (2026-07-20 extension): the "remove" action — sibling to
        // "approve" above in every respect except what it does to the row:
        // bulk-capable from the start (single-row is just the N=1 case),
        // same per-id success/failure reporting shape, same "Admin" policy,
        // always 200 with a per-id result rather than an all-or-nothing
        // batch outcome. Unlike approve, a row doesn't need to still be
        // "unverified" to be removed — see
        // IPlayerStoreRepository.RemovePlayerDataAsync's own comment for why.
        //
        // Audit logging: this hard-deletes the row, so there is nothing left
        // to attach an "ApprovedByAdminId/ApprovedAt"-shaped pair of columns
        // to (see PlayerData.cs's comment on why that shape doesn't apply to
        // removal). "The action is logged with admin_id and a timestamp"
        // (REQ-503) is satisfied here by a structured ILogger line per
        // successfully-removed row, rather than a new audit-log table —
        // this codebase has deliberately avoided a general-purpose one so
        // far (PlayerOverride/PlayerData's own audit columns instead).
        app.MapPost("/admin/player-data/remove", async (
            RemovePlayerDataRequest request,
            ClaimsPrincipal principal,
            // S-106 (pure refactor): RemovePlayerDataAsync moved to
            // IPlayerDataRepository.
            IPlayerDataRepository playerDataRepository,
            ILogger<AdminEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (request.PlayerDataIds is null || request.PlayerDataIds.Count == 0)
            {
                return Results.Problem(
                    title: "Invalid removal request",
                    detail: "playerDataIds must contain at least one id.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var outcomes = await playerDataRepository.RemovePlayerDataAsync(request.PlayerDataIds, cancellationToken);

            var removedAt = DateTime.UtcNow;
            foreach (var outcome in outcomes)
            {
                if (!outcome.Removed)
                    continue;

                logger.LogInformation(
                    "Admin {AdminId} removed PlayerData {PlayerDataId} at {RemovedAt}",
                    adminId, outcome.PlayerDataId, removedAt);
            }

            var results = outcomes
                .Select(o => new PlayerDataRemovalResult(o.PlayerDataId, o.Removed, o.FailureReason?.ToString()))
                .ToList();

            return Results.Ok(new RemovePlayerDataResponse(results));
        }).RequireAuthorization("Admin");

        // REQ-501: creating an override always wins over cached
        // PlayerData/PlayerAttribute for the same (PlayerId, Field) — see
        // ADR-0015 for exactly what "wins" means (replaces the whole
        // attribute type, not one value within it).
        app.MapPost("/admin/player-overrides", async (
            CreatePlayerOverrideRequest request,
            ClaimsPrincipal principal,
            IPlayerOverrideRepository playerOverrideRepository,
            IPlayerRepository playerRepository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Field) || string.IsNullOrWhiteSpace(request.Value) || string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.Problem(
                    title: "Invalid override",
                    detail: "field, value, and reason are all required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var player = await playerRepository.GetPlayerByIdAsync(request.PlayerId, cancellationToken);
            if (player is null)
                return Results.NotFound();

            var existing = await playerOverrideRepository.GetOverrideAsync(request.PlayerId, request.Field, cancellationToken);
            if (existing is not null)
            {
                return Results.Problem(
                    title: "Override already exists",
                    detail: $"An override for field '{request.Field}' already exists for this player — use PUT /admin/player-overrides/{{id}} to update it.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var playerOverride = new PlayerOverride
            {
                Id = Guid.NewGuid(),
                PlayerId = request.PlayerId,
                Field = request.Field,
                Value = request.Value,
                Reason = request.Reason,
                // Policy above already required a valid "sub" claim to reach here.
                LockedByAdminId = principal.GetAuthProviderUserId()!.Value,
                LockedAt = DateTime.UtcNow,
            };
            await playerOverrideRepository.AddOverrideAsync(playerOverride, cancellationToken);

            return Results.Created($"/admin/player-overrides/{playerOverride.Id}", ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapGet("/admin/player-overrides/{id:guid}", async (
            Guid id,
            IPlayerOverrideRepository playerOverrideRepository,
            CancellationToken cancellationToken) =>
        {
            var playerOverride = await playerOverrideRepository.GetOverrideByIdAsync(id, cancellationToken);
            return playerOverride is null ? Results.NotFound() : Results.Ok(ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapPut("/admin/player-overrides/{id:guid}", async (
            Guid id,
            UpdatePlayerOverrideRequest request,
            ClaimsPrincipal principal,
            IPlayerOverrideRepository playerOverrideRepository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value) || string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.Problem(
                    title: "Invalid override",
                    detail: "value and reason are required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var playerOverride = await playerOverrideRepository.GetOverrideByIdAsync(id, cancellationToken);
            if (playerOverride is null)
                return Results.NotFound();

            playerOverride.Value = request.Value;
            playerOverride.Reason = request.Reason;
            // Policy above already required a valid "sub" claim to reach here.
            playerOverride.LockedByAdminId = principal.GetAuthProviderUserId()!.Value;
            playerOverride.LockedAt = DateTime.UtcNow;
            await playerOverrideRepository.UpdateOverrideAsync(playerOverride, cancellationToken);

            return Results.Ok(ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapDelete("/admin/player-overrides/{id:guid}", async (
            Guid id,
            IPlayerOverrideRepository playerOverrideRepository,
            CancellationToken cancellationToken) =>
        {
            var deleted = await playerOverrideRepository.DeleteOverrideAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Admin");

        // REQ-513 (GitHub issue #239): admin re-fetch of a single Player's
        // FullName/Position/BirthYear/PhotoUrl from Wikidata, using the
        // player's own already-stored WikidataQid — never an admin-supplied
        // value for any of these fields (contrast REQ-501's
        // POST /admin/player-overrides above, which does accept an
        // admin-typed value). Registered unconditionally, including
        // Production, same as every other endpoint in this file (unlike
        // REQ-505/506's non-Production-only tools, AdminManagementEndpoints.cs)
        // — this action's whole purpose is correcting real player data that
        // goes wrong in production, so restricting it to non-Production
        // would defeat the requirement that prompted it (issue #239's
        // permanently-corrupted cached name, with no way to correct it).
        //
        // Per-field diff, not a blanket rewrite (REQ-513's own acceptance
        // criteria): a differing non-null/non-empty fetched value overwrites
        // the existing Player field; a null/missing Wikidata binding for a
        // field never overwrites (absence isn't evidence the cached value is
        // wrong — the same "absence is not evidence of wrongness" principle
        // ADR-0046 already establishes for a guess-time timeout); an
        // identical value is a no-op for that field. No `reason` field —
        // unlike REQ-501's PlayerOverride "correct" action, this re-applies
        // data already trusted by default at sync time (ADR-0032), not a new
        // manual admin judgment call — same "no reason needed" precedent as
        // the "approve" action above.
        //
        // Audit trail: Player has no admin-audit columns of its own (unlike
        // PlayerOverride's LockedByAdminId/LockedAt) — recorded via a
        // structured ILogger line instead, same "no row to attach an audit
        // trail to -> structured log line" precedent as the "remove" action
        // above. Logged unconditionally, whether or not any field actually
        // changed, since REQ-513's own acceptance criterion is "the action
        // is recorded... at refresh time," not "only a refresh that changed
        // something."
        app.MapPost("/admin/players/{id:guid}/refresh-from-wikidata", async (
            Guid id,
            ClaimsPrincipal principal,
            IPlayerRepository playerRepository,
            IWikidataClient wikidataClient,
            ILogger<AdminEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            // REQ-513's one deliberate, narrow exception to "Player fields
            // are set once at creation, never touched again" — see
            // IPlayerRepository.GetPlayerForRefreshAsync's own doc comment.
            var player = await playerRepository.GetPlayerForRefreshAsync(id, cancellationToken);
            if (player is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(player.WikidataQid))
            {
                return Results.Problem(
                    title: "No Wikidata QID to refresh from",
                    detail: "This player has no WikidataQid on record — there is nothing to refresh from. This action never falls back to a name-based search (see REQ-510's separate standalone search-and-add path for that).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Captured into a local, non-null variable rather than relying on
            // flow-analysis narrowing of player.WikidataQid to persist across
            // the two `await`s below — same defensive style as this file's
            // existing "Policy above already required..." comments.
            var wikidataQid = player.WikidataQid;

            WikidataPlayerRefreshData refreshed;
            try
            {
                refreshed = await wikidataClient.QueryPlayerRefreshDataByQidAsync(wikidataQid, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                // Same ADR-0046 timeout-vs-no-match distinction, and the same
                // server-side-only logging, as AdminSuggestionEndpoints.cs's
                // two /lookup endpoints — see that file's own catch block for
                // the full reasoning. The exception's own Message is
                // deliberately NOT surfaced to the caller (this endpoint's
                // caller is an admin's browser, not a scheduled job's own CI
                // log — the /internal/* Message-as-detail carve-out in
                // docs/coding-guidelines.md doesn't apply here).
                logger.LogWarning(
                    ex,
                    "Wikidata refresh failed for Player {PlayerId} (WikidataQid {WikidataQid})",
                    id, wikidataQid);
                return Results.Problem(
                    title: "Live verification unavailable",
                    detail: "We couldn't reach Wikidata to refresh this player. Please try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var oldFullName = player.FullName;
            var oldPosition = player.Position;
            var oldBirthYear = player.BirthYear;
            var oldPhotoUrl = player.PhotoUrl;

            // Per-field diff (REQ-513): a non-null/non-empty fetched value
            // that differs from the current value overwrites it; a null/
            // empty fetched value (Wikidata has no current binding for that
            // property) or an identical value never writes — see this
            // endpoint's own header comment above for the full reasoning.
            var fullNameChanged = !string.IsNullOrWhiteSpace(refreshed.FullName) && refreshed.FullName != oldFullName;
            var positionChanged = !string.IsNullOrWhiteSpace(refreshed.Position) && refreshed.Position != oldPosition;
            var birthYearChanged = refreshed.BirthYear is not null && refreshed.BirthYear != oldBirthYear;
            var photoUrlChanged = !string.IsNullOrWhiteSpace(refreshed.PhotoUrl) && refreshed.PhotoUrl != oldPhotoUrl;

            if (fullNameChanged)
                player.FullName = refreshed.FullName!;
            if (positionChanged)
                player.Position = refreshed.Position;
            if (birthYearChanged)
                player.BirthYear = refreshed.BirthYear;
            if (photoUrlChanged)
                player.PhotoUrl = refreshed.PhotoUrl;

            if (fullNameChanged || positionChanged || birthYearChanged || photoUrlChanged)
                await playerRepository.UpdatePlayerAsync(player, cancellationToken);

            var fieldResults = new List<PlayerRefreshFieldResult>
            {
                new("fullName", fullNameChanged, oldFullName, fullNameChanged ? player.FullName : null),
                new("position", positionChanged, oldPosition, positionChanged ? player.Position : null),
                new("birthYear", birthYearChanged, oldBirthYear?.ToString(), birthYearChanged ? player.BirthYear?.ToString() : null),
                new("photoUrl", photoUrlChanged, oldPhotoUrl, photoUrlChanged ? player.PhotoUrl : null),
            };

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            logger.LogInformation(
                "Admin {AdminId} refreshed Player {PlayerId} (WikidataQid {WikidataQid}) from Wikidata: {FieldChanges}",
                adminId, id, wikidataQid,
                string.Join("; ", fieldResults.Select(f =>
                    f.Changed ? $"{f.Field}: '{f.OldValue}' -> '{f.NewValue}'" : $"{f.Field}: unchanged")));

            return Results.Ok(new RefreshPlayerFromWikidataResponse(id, wikidataQid, fieldResults));
        }).RequireAuthorization("Admin");
    }

    private static PlayerOverrideResponse ToResponse(PlayerOverride playerOverride) =>
        new(playerOverride.Id, playerOverride.PlayerId, playerOverride.Field, playerOverride.Value,
            playerOverride.Reason, playerOverride.LockedByAdminId, playerOverride.LockedAt);
}

// Pure log-category marker for ILogger<T> — same pattern as
// AdminManagementEndpoints.AdminManagementLogCategory.
internal sealed class AdminEndpointsLogCategory;

public record UnverifiedPlayerDataResponse(
    Guid Id, Guid PlayerId, string PlayerFullName, string Field, string Value, string Source, string Confidence, DateTime SyncedAt);

public record ApprovePlayerDataRequest(IReadOnlyList<Guid> PlayerDataIds);

// FailureReason is null when Approved is true; otherwise the string form of
// PlayerDataApprovalFailureReason (XGArcade.Data.Repositories) — "NotFound"
// or "NotUnverified" — kept as a plain string at the API boundary rather
// than serializing the repository-layer enum type directly.
public record PlayerDataApprovalResult(Guid PlayerDataId, bool Approved, string? FailureReason);

public record ApprovePlayerDataResponse(IReadOnlyList<PlayerDataApprovalResult> Results);

public record RemovePlayerDataRequest(IReadOnlyList<Guid> PlayerDataIds);

// FailureReason is null when Removed is true; otherwise the string form of
// PlayerDataRemovalFailureReason (XGArcade.Data.Repositories) — only
// "NotFound" exists for removal, unlike approve's two reasons (see
// RemovePlayerDataAsync's own comment for why "NotUnverified" doesn't apply
// here) — kept as a plain string at the API boundary rather than
// serializing the repository-layer enum type directly.
public record PlayerDataRemovalResult(Guid PlayerDataId, bool Removed, string? FailureReason);

public record RemovePlayerDataResponse(IReadOnlyList<PlayerDataRemovalResult> Results);

public record CreatePlayerOverrideRequest(Guid PlayerId, string Field, string Value, string Reason);

public record UpdatePlayerOverrideRequest(string Value, string Reason);

public record PlayerOverrideResponse(Guid Id, Guid PlayerId, string Field, string Value, string Reason, Guid LockedByAdminId, DateTime LockedAt);

// REQ-513 (GitHub issue #239): one of the four scalar Player fields
// (fullName/position/birthYear/photoUrl) this refresh action can touch.
// OldValue is always the value BEFORE this refresh ran, regardless of
// Changed; NewValue is populated only when Changed is true — an unchanged
// field has nothing new to report, so this deliberately doesn't repeat
// OldValue into NewValue for the unchanged case. birthYear's int? is
// serialized as its string form here (same as every other field) rather
// than adding a differently-typed sibling record just for one field —
// this response exists purely for an admin to read, never re-parsed by any
// other endpoint.
public record PlayerRefreshFieldResult(string Field, bool Changed, string? OldValue, string? NewValue);

public record RefreshPlayerFromWikidataResponse(Guid PlayerId, string WikidataQid, IReadOnlyList<PlayerRefreshFieldResult> Fields);
