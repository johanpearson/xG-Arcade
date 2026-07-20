using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

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
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            var unverified = await playerStoreRepository.GetUnverifiedPlayerDataAsync(cancellationToken);

            var playerIds = unverified.Select(data => data.PlayerId).Distinct().ToList();
            var playersById = await playerStoreRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

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
            IPlayerStoreRepository playerStoreRepository,
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
            var outcomes = await playerStoreRepository.ApprovePlayerDataAsync(request.PlayerDataIds, adminId, cancellationToken);

            var results = outcomes
                .Select(o => new PlayerDataApprovalResult(o.PlayerDataId, o.Approved, o.FailureReason?.ToString()))
                .ToList();

            return Results.Ok(new ApprovePlayerDataResponse(results));
        }).RequireAuthorization("Admin");

        // REQ-501: creating an override always wins over cached
        // PlayerData/PlayerAttribute for the same (PlayerId, Field) — see
        // ADR-0015 for exactly what "wins" means (replaces the whole
        // attribute type, not one value within it).
        app.MapPost("/admin/player-overrides", async (
            CreatePlayerOverrideRequest request,
            ClaimsPrincipal principal,
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Field) || string.IsNullOrWhiteSpace(request.Value) || string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.Problem(
                    title: "Invalid override",
                    detail: "field, value, and reason are all required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var player = await playerStoreRepository.GetPlayerByIdAsync(request.PlayerId, cancellationToken);
            if (player is null)
                return Results.NotFound();

            var existing = await playerStoreRepository.GetOverrideAsync(request.PlayerId, request.Field, cancellationToken);
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
            await playerStoreRepository.AddOverrideAsync(playerOverride, cancellationToken);

            return Results.Created($"/admin/player-overrides/{playerOverride.Id}", ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapGet("/admin/player-overrides/{id:guid}", async (
            Guid id,
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            var playerOverride = await playerStoreRepository.GetOverrideByIdAsync(id, cancellationToken);
            return playerOverride is null ? Results.NotFound() : Results.Ok(ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapPut("/admin/player-overrides/{id:guid}", async (
            Guid id,
            UpdatePlayerOverrideRequest request,
            ClaimsPrincipal principal,
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value) || string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.Problem(
                    title: "Invalid override",
                    detail: "value and reason are required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var playerOverride = await playerStoreRepository.GetOverrideByIdAsync(id, cancellationToken);
            if (playerOverride is null)
                return Results.NotFound();

            playerOverride.Value = request.Value;
            playerOverride.Reason = request.Reason;
            // Policy above already required a valid "sub" claim to reach here.
            playerOverride.LockedByAdminId = principal.GetAuthProviderUserId()!.Value;
            playerOverride.LockedAt = DateTime.UtcNow;
            await playerStoreRepository.UpdateOverrideAsync(playerOverride, cancellationToken);

            return Results.Ok(ToResponse(playerOverride));
        }).RequireAuthorization("Admin");

        app.MapDelete("/admin/player-overrides/{id:guid}", async (
            Guid id,
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            var deleted = await playerStoreRepository.DeleteOverrideAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Admin");
    }

    private static PlayerOverrideResponse ToResponse(PlayerOverride playerOverride) =>
        new(playerOverride.Id, playerOverride.PlayerId, playerOverride.Field, playerOverride.Value,
            playerOverride.Reason, playerOverride.LockedByAdminId, playerOverride.LockedAt);
}

public record UnverifiedPlayerDataResponse(
    Guid Id, Guid PlayerId, string PlayerFullName, string Field, string Value, string Source, string Confidence, DateTime SyncedAt);

public record ApprovePlayerDataRequest(IReadOnlyList<Guid> PlayerDataIds);

// FailureReason is null when Approved is true; otherwise the string form of
// PlayerDataApprovalFailureReason (XGArcade.Data.Repositories) — "NotFound"
// or "NotUnverified" — kept as a plain string at the API boundary rather
// than serializing the repository-layer enum type directly.
public record PlayerDataApprovalResult(Guid PlayerDataId, bool Approved, string? FailureReason);

public record ApprovePlayerDataResponse(IReadOnlyList<PlayerDataApprovalResult> Results);

public record CreatePlayerOverrideRequest(Guid PlayerId, string Field, string Value, string Reason);

public record UpdatePlayerOverrideRequest(string Value, string Reason);

public record PlayerOverrideResponse(Guid Id, Guid PlayerId, string Field, string Value, string Reason, Guid LockedByAdminId, DateTime LockedAt);
