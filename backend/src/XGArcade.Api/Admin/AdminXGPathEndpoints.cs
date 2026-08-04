using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// REQ-1209/ADR-0058: admin-only read of xG Path's REQ-1208 target-selection
// cycle state (COMP-11's own persisted data). Registered unconditionally,
// same as AdminAccountsEndpoints.cs's REQ-507/508 endpoints — this reads
// real, always-relevant operational state, not seeded/test data, so there's
// no reason to gate it to non-Production the way AdminManagementEndpoints.cs
// (REQ-505/506) does. Kept in its own file, mirroring how
// AdminAccountsEndpoints.cs/AdminManagementEndpoints.cs are already split
// out from AdminEndpoints.cs rather than growing that file further.
public static class AdminXGPathEndpoints
{
    public static void MapAdminXGPathEndpoints(this WebApplication app)
    {
        // REQ-1209: reads only IPathInstanceRepository.GetCycleStateAsync —
        // a pure read of already-persisted state (PathTargetCycle). Never
        // calls IPlayerFamiliarityService, never touches
        // GetEligiblePlayerIdsAsync/PickDistinct, never triggers round
        // generation — this endpoint has no route into
        // XGPathGameModule.GenerateInstanceAsync at all, which is what
        // guarantees REQ-1209's "never itself triggers a new eligible-pool
        // computation or a live Wikidata familiarity check" requirement.
        app.MapGet("/admin/xg-path/cycle", async (
            IPathInstanceRepository pathInstanceRepository,
            CancellationToken cancellationToken) =>
        {
            var cycleState = await pathInstanceRepository.GetCycleStateAsync(cancellationToken);

            // REQ-1209: "no xG Path round has ever been generated yet" is
            // simply "no PathTargetCycle row exists yet" — surfaced as
            // HasData=false with every other field null, rather than a 404,
            // so the frontend's "no data yet" state is a plain
            // successful-response branch, not an error branch.
            if (cycleState is null)
            {
                return Results.Ok(new AdminXGPathCycleResponse(
                    HasData: false,
                    CycleNumber: null,
                    ObservedPoolSize: null,
                    UsedInCycleCount: null,
                    RemainingInCycleCount: null,
                    LastCycleCompletedAt: null));
            }

            var remainingInCycle = cycleState.ObservedPoolSize - cycleState.UsedInCycleCount;
            return Results.Ok(new AdminXGPathCycleResponse(
                HasData: true,
                CycleNumber: cycleState.CycleNumber,
                ObservedPoolSize: cycleState.ObservedPoolSize,
                UsedInCycleCount: cycleState.UsedInCycleCount,
                RemainingInCycleCount: remainingInCycle,
                LastCycleCompletedAt: cycleState.LastCycleCompletedAt));
        }).RequireAuthorization("Admin");
    }
}

// REQ-1209: RemainingInCycleCount is derived here (ObservedPoolSize -
// UsedInCycleCount) rather than persisted as its own column — it's fully
// determined by the other two persisted figures, so storing it separately
// would just be a third value that could drift out of sync with them.
// HasData=false is the "no xG Path round has ever generated" case (every
// other field null/zero) — same shaped-empty-response pattern
// AdminAccountMetricsResponse's callers already expect from a successful
// admin GET, not an error.
public record AdminXGPathCycleResponse(
    bool HasData,
    int? CycleNumber,
    int? ObservedPoolSize,
    int? UsedInCycleCount,
    int? RemainingInCycleCount,
    DateTime? LastCycleCompletedAt);
