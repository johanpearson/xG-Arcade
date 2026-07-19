using XGArcade.Core.Auth;
using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// S-026 (docs/backlog.md): REQ-505 (admin round control) and REQ-506 (admin
// user deletion) — both non-Production-only, unlike S-012's REQ-501/502/503
// endpoints in AdminEndpoints.cs, which have no such restriction. Kept in
// their own file/registration method specifically so that distinction is
// visible at a glance, not buried as a per-endpoint condition inside
// MapAdminEndpoints.
//
// Fail-closed per ADR-0006: the routes below are never registered at all
// when ASPNETCORE_ENVIRONMENT == Production, checked here before routing —
// same discipline as InternalRoundEndpoints' REQ-806/807 test-data routes —
// never guarded only by the "Admin" authorization policy alone. Both
// protections apply: the environment gate (fail-closed even if "Admin" were
// ever misconfigured) and the "Admin" policy itself (so a non-admin
// developer hitting a non-Production environment still can't use these).
public static class AdminManagementEndpoints
{
    public static void MapAdminManagementEndpoints(this WebApplication app)
    {
        if (app.Environment.IsProduction())
            return;

        // REQ-505: lets the admin UI show what it would be acting on before
        // the admin actually ends the round or changes its schedule. Always
        // 200 (HasActiveRound: false rather than a 404) when the round
        // control feature itself is present — this is also the frontend's
        // only reliable way to tell "this environment has no active round
        // right now" apart from "this route doesn't exist here at all"
        // (Production): the latter is a real 404 from routing itself, since
        // this whole method returns before mapping anything when
        // IsProduction() is true.
        app.MapGet("/admin/rounds/{gameKey}/active", async (
            string gameKey,
            IRoundRepository roundRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var round = await roundRepository.GetActiveByGameKeyAsync(gameKey, now, cancellationToken);

            return Results.Ok(new AdminActiveRoundResponse(round is not null, round is null ? null : ToResponse(round)));
        }).RequireAuthorization("Admin");

        // REQ-505: the human-facing, admin-authenticated equivalent of
        // REQ-806's POST /internal/test-data/force-close-round/{roundId} —
        // that endpoint needs the round id and the INTERNAL_JOB_TOKEN bearer
        // (meant for automated tests); this one resolves the active round
        // for the caller, from an admin's own login, and reuses the exact
        // same IRoundCloseService.CloseRoundAsync (REQ-205) rather than a
        // second, independently-written close path.
        app.MapPost("/admin/rounds/{gameKey}/close", async (
            string gameKey,
            IRoundRepository roundRepository,
            IRoundCloseService roundCloseService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var activeRound = await roundRepository.GetActiveByGameKeyAsync(gameKey, now, cancellationToken);
            if (activeRound is null)
                return Results.NotFound();

            var closedRound = await roundCloseService.CloseRoundAsync(activeRound.Id, now, cancellationToken);

            // Can't actually be null here (CloseRoundAsync only returns null
            // for an unknown round id, and activeRound.Id was just read from
            // the DB above) — the null-check stays anyway rather than a
            // force-unwrap, since CloseRoundAsync's own contract allows it.
            return closedRound is null ? Results.NotFound() : Results.Ok(ToResponse(closedRound));
        }).RequireAuthorization("Admin");

        // REQ-505's second capability, which REQ-806 doesn't cover at all:
        // adjusting the active round's schedule rather than only closing it
        // immediately. Can only push end_time later or to a still-future
        // point — never used to retroactively close an already-ended round
        // (REQ-205's lock behavior already owns that path); the close
        // endpoint above is the only way to end a round *now*.
        app.MapPut("/admin/rounds/{gameKey}/end-time", async (
            string gameKey,
            UpdateRoundEndTimeRequest request,
            IRoundRepository roundRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var activeRound = await roundRepository.GetActiveByGameKeyAsync(gameKey, now, cancellationToken);
            if (activeRound is null)
                return Results.NotFound();

            if (request.EndTime <= activeRound.StartTime || request.EndTime <= now)
            {
                return Results.Problem(
                    title: "Invalid end time",
                    detail: "The new end time must be after the round's start time and after the current time.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // GetActiveByGameKeyAsync reads AsNoTracking (RoundRepository) —
            // re-fetched by id here for a tracked entity to mutate and save,
            // same read-then-write shape RoundCloseService itself uses.
            var round = await roundRepository.GetByIdAsync(activeRound.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Round '{activeRound.Id}' was active moments ago and no longer exists.");

            round.EndTime = request.EndTime;
            await roundRepository.UpdateAsync(round, cancellationToken);

            return Results.Ok(ToResponse(round));
        }).RequireAuthorization("Admin");

        // REQ-506: reuses REQ-710's own anonymize/delete implementation
        // (IAccountDeletionService) rather than a second, independently-
        // written deletion path — the only new thing here is how the target
        // user is identified (by email, the one identifier an admin actually
        // has to hand) and that an admin's own authorization is the
        // confirmation step, in place of self-service's re-entered password.
        app.MapDelete("/admin/users", async (
            string email,
            IUserRepository userRepository,
            IAccountDeletionService accountDeletionService,
            ILogger<AdminManagementLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var user = await userRepository.GetByEmailAsync(email, cancellationToken);
            if (user is null)
                return Results.NotFound();

            var result = await accountDeletionService.DeleteAccountAsync(user.Id, cancellationToken);
            if (!result.Success)
            {
                logger.LogError(
                    "Admin-triggered account deletion failed for user {UserId}: {ErrorMessage}",
                    user.Id, result.ErrorMessage);

                return Results.Problem(
                    title: "Account deletion failed",
                    detail: result.ErrorMessage,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

    private static AdminRoundResponse ToResponse(Round round) =>
        new(round.Id, round.GameKey, round.StartTime, round.EndTime);
}

public record AdminRoundResponse(Guid RoundId, string GameKey, DateTime StartTime, DateTime EndTime);

// HasActiveRound: false + Round: null is a normal, expected state (no round
// currently active for this game) — never conflated with the route itself
// being absent (a real 404), see the endpoint's own doc comment above.
public record AdminActiveRoundResponse(bool HasActiveRound, AdminRoundResponse? Round);

public record UpdateRoundEndTimeRequest(DateTime EndTime);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalRoundEndpoints.RoundGenerationLogCategory.
internal sealed class AdminManagementLogCategory;
