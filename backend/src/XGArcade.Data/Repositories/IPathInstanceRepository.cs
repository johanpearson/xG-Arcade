using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGPath's (COMP-11) own persistence — the only path Games.XGPath
// reaches PathTemplate/PathInstance/PathPuzzle through, same
// repository-per-component pattern as IGridInstanceRepository (COMP-05).
public interface IPathInstanceRepository
{
    Task<PathTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // S-084/REQ-1202: mirrors IGridInstanceRepository.GetTemplateBySizeAsync/
    // AddTemplateAsync's exact find-or-create-by-config-value pattern —
    // PathTemplateResolver (XGArcade.Api.Path) is the caller, same role
    // GridTemplateResolver plays for GridTemplate.
    Task<PathTemplate?> GetTemplateByPuzzleCountAsync(int puzzleCount, CancellationToken cancellationToken = default);
    Task<PathTemplate> AddTemplateAsync(PathTemplate template, CancellationToken cancellationToken = default);

    Task<PathInstance> AddInstanceAsync(PathInstance instance, CancellationToken cancellationToken = default);
    Task<PathInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-1208/ADR-0058: pure read, never creates a row — this is the only
    // method the admin read endpoint (REQ-1209) is allowed to call, since
    // REQ-1209 requires reading already-persisted state only, never
    // triggering round generation or a live eligible-pool computation. Null
    // means "no xG Path round has ever generated" (REQ-1209's "no data yet"
    // case) — the caller renders that state directly rather than treating
    // it as an error.
    Task<PathTargetCycle?> GetCycleStateAsync(CancellationToken cancellationToken = default);

    // REQ-1208/ADR-0058: idempotent singleton lookup, mirroring
    // ILeagueRepository.GetOrCreateGlobalLeagueAsync's exact "first call
    // ever made creates the one row; every later call returns the same
    // row" shape — the initial row starts at CycleNumber 1 with
    // ObservedPoolSize/UsedInCycleCount both 0 and no completed-cycle
    // timestamp. Only XGPathGameModule.GenerateInstanceAsync calls this
    // (never the admin read endpoint — see GetCycleStateAsync above).
    Task<PathTargetCycle> GetOrCreateCycleStateAsync(CancellationToken cancellationToken = default);

    // REQ-1208: every distinct PlayerId recorded as used as a target since
    // `cycleNumber`'s cycle began. A player no longer present in the live
    // eligible pool this generation simply has their id ignored by the
    // caller's own Except-style filtering — this method itself has no
    // opinion on eligibility, only on "was this id ever recorded as used in
    // this cycle number."
    Task<IReadOnlyList<Guid>> GetUsedPlayerIdsInCycleAsync(int cycleNumber, CancellationToken cancellationToken = default);

    // REQ-1208/ADR-0058: persists a completed generation's PathInstance +
    // Puzzles (same write AddInstanceAsync already performs) together with
    // this generation's resolved cycle state (`cycleState` — already
    // advanced by the caller if this generation triggered a rollover) and
    // one PathCycleTargetUsage row per selected target, all in the SAME
    // SaveChangesAsync call/unit of work — so the puzzle-target write and
    // the "recorded as used in the current cycle" write can never diverge
    // on a partial failure, per REQ-1208's own "at the same time" wording.
    // Load-then-save, not ExecuteUpdateAsync/ExecuteDeleteAsync
    // (coding-guidelines.md — the InMemory test provider can't translate
    // those).
    Task<PathInstance> AddInstanceWithCycleUsageAsync(
        PathInstance instance,
        PathTargetCycle cycleState,
        IReadOnlyCollection<Guid> targetPlayerIds,
        CancellationToken cancellationToken = default);
}
