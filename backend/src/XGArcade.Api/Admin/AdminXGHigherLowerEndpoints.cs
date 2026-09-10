using XGArcade.Data.Repositories;
using XGArcade.Games.XGHigherLower;

namespace XGArcade.Api.Admin;

// REQ-1507/ADR-0112: admin-only read of xG Higher/Lower's real international-
// caps/goals data coverage — the "get real Wikidata coverage numbers before
// fully trusting this" follow-up ADR-0112's own Consequences section
// explicitly flagged as an open risk. REQ-1501/ADR-0113: this endpoint's
// PlayersWithTrophyCount already reads GetEffectivePlayerCountsByAttributeTypeAsync
// ("trophy") — ADR-0113's new PlayerTrophyStatsRefreshService/
// PlayerTrophyStatsBackfillService sweep is a new WRITER feeding that same
// read, not a new reader shape, so this endpoint needed no code change to
// report real post-sweep trophy coverage; confirmed, not modified. Mirrors AdminXGPathEndpoints.cs's exact
// shape: registered unconditionally (including Production — real operational
// state, not seeded/test data), gated on the same "Admin" policy every other
// admin endpoint uses, and a PURE READ of already-persisted repository
// state — never triggers Round generation, never calls
// IPlayerInternationalStatsRefreshService/PlayerInternationalStatsBackfillService,
// never touches Wikidata itself. Kept in its own file, same "one feature, one
// file" convention as AdminXGPathEndpoints.cs/AdminAccountsEndpoints.cs.
public static class AdminXGHigherLowerEndpoints
{
    public static void MapAdminXGHigherLowerEndpoints(this WebApplication app)
    {
        // REQ-1507: reuses IPlayerOverrideRepository's two already-built,
        // already-correctness-tested effective-value reads
        // (GetEffectivePlayerValuesByAttributeTypeAsync for the
        // single-recorded-value caps/goals shape, GetEffectivePlayerCountsByAttributeTypeAsync
        // for trophy's count-of-rows shape — both already handle override
        // precedence and the absent-not-zero contract correctly, ADR-0112/
        // ADR-0111) rather than a new raw query — this endpoint only counts
        // and reports, it never reimplements eligibility logic.
        app.MapGet("/admin/xg-higher-lower/international-stats-coverage", async (
            IPlayerRepository playerRepository,
            IPlayerOverrideRepository playerOverrideRepository,
            HigherLowerGenerationOptions higherLowerGenerationOptions,
            CancellationToken cancellationToken) =>
        {
            // Sequential, not Task.WhenAll: these calls share the request-
            // scoped XGArcadeDbContext via the repositories above, and
            // concurrent use of one DbContext is unsafe in EF Core (works
            // against the InMemory test provider, throws against real
            // Npgsql) — same discipline AdminAccountsEndpoints.cs's
            // /admin/accounts/metrics already follows for its own sequence
            // of repository calls.
            var totalPlayerCount = await playerRepository.CountPlayersAsync(cancellationToken);

            var capsByPlayerId = await playerOverrideRepository.GetEffectivePlayerValuesByAttributeTypeAsync(
                InternationalCapsAttributeType, cancellationToken);
            var goalsByPlayerId = await playerOverrideRepository.GetEffectivePlayerValuesByAttributeTypeAsync(
                InternationalGoalsAttributeType, cancellationToken);
            var trophyCountsByPlayerId = await playerOverrideRepository.GetEffectivePlayerCountsByAttributeTypeAsync(
                TrophyAttributeType, cancellationToken);

            // REQ-1506's own floor: the number that actually matters for
            // whether that requirement's eligible pool is viable in
            // practice — a plain .Count(v >= threshold) over the same
            // effective-caps dictionary REQ-1506's generation-time read
            // already uses, never a reimplementation of the floor check
            // itself.
            var playersMeetingCapsFloorCount = capsByPlayerId.Values
                .Count(caps => caps >= higherLowerGenerationOptions.MinimumInternationalCaps);

            return Results.Ok(new AdminXGHigherLowerInternationalStatsCoverageResponse(
                TotalPlayerCount: totalPlayerCount,
                PlayersWithCapsCount: capsByPlayerId.Count,
                PlayersWithGoalsCount: goalsByPlayerId.Count,
                PlayersWithTrophyCount: trophyCountsByPlayerId.Count,
                PlayersMeetingCapsFloorCount: playersMeetingCapsFloorCount,
                CapsFloorThreshold: higherLowerGenerationOptions.MinimumInternationalCaps));
        }).RequireAuthorization("Admin");
    }

    // AttributeType literals, matching this codebase's established
    // convention (see PlayerInternationalStatsRefreshService.cs's own
    // comment on why these aren't shared constants across projects) — must
    // stay in sync with HigherLowerGenerationService.CandidateStatCategories/
    // InternationalCapsAttributeType and
    // PlayerInternationalStatsRefreshService.CapsAttributeType/GoalsAttributeType.
    private const string InternationalCapsAttributeType = "international-caps";
    private const string InternationalGoalsAttributeType = "international-goals";
    private const string TrophyAttributeType = "trophy";
}

// REQ-1507: TotalPlayerCount is every Player row (IPlayerRepository.CountPlayersAsync,
// not scoped to WikidataQid presence — an admin reading this wants the true
// denominator). PlayersWithCapsCount/PlayersWithGoalsCount/PlayersWithTrophyCount
// are each the number of players with a real, non-null EFFECTIVE value for
// that AttributeType (override-aware, ADR-0015/ADR-0111/ADR-0112) — never a
// raw PlayerAttribute row count, which could double-count a player with
// multiple trophy rows or miscount one an admin override has replaced.
// PlayersMeetingCapsFloorCount is the subset of PlayersWithCapsCount whose
// effective caps value is >= CapsFloorThreshold (REQ-1506's own floor,
// HigherLowerGenerationOptions.MinimumInternationalCaps) — the number that
// actually determines REQ-1506's eligible-pool viability, echoed back as
// CapsFloorThreshold so a caller never has to know that default out of band.
public record AdminXGHigherLowerInternationalStatsCoverageResponse(
    int TotalPlayerCount,
    int PlayersWithCapsCount,
    int PlayersWithGoalsCount,
    int PlayersWithTrophyCount,
    int PlayersMeetingCapsFloorCount,
    int CapsFloorThreshold);
