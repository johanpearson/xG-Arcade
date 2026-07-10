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
        // confidence visible for each row.
        app.MapGet("/admin/player-data/unverified", async (
            IPlayerStoreRepository playerStoreRepository,
            CancellationToken cancellationToken) =>
        {
            var unverified = await playerStoreRepository.GetUnverifiedPlayerDataAsync(cancellationToken);

            var responses = new List<UnverifiedPlayerDataResponse>(unverified.Count);
            foreach (var data in unverified)
            {
                var player = await playerStoreRepository.GetPlayerByIdAsync(data.PlayerId, cancellationToken);
                responses.Add(new UnverifiedPlayerDataResponse(
                    data.Id, data.PlayerId, player?.FullName ?? string.Empty,
                    data.Field, data.Value, data.Source, data.Confidence, data.SyncedAt));
            }

            return Results.Ok(responses);
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

public record CreatePlayerOverrideRequest(Guid PlayerId, string Field, string Value, string Reason);

public record UpdatePlayerOverrideRequest(string Value, string Reason);

public record PlayerOverrideResponse(Guid Id, Guid PlayerId, string Field, string Value, string Reason, Guid LockedByAdminId, DateTime LockedAt);
