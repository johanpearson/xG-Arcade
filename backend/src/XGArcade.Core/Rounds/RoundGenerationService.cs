using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Rounds;

// COMP-03: REQ-301's "generate one round ahead" scheduling rule.
//
// The rule is deliberately framed as an idempotency check, not a counter:
// if a round for this GameKey hasn't started yet, that upcoming round IS
// "round N+1" — nothing to do until it becomes active itself, however many
// times the scheduler job fires in the meantime. This is what makes the
// job safe to trigger more often than strictly necessary (a manual
// workflow_dispatch, a retried cron run) without ever accumulating extra
// rounds ahead of the active one.
public class RoundGenerationService(
    IRoundRepository roundRepository,
    IGameModuleResolver gameModuleResolver,
    IRoundCloseService roundCloseService,
    IRoundSchedulingOptionsResolver roundSchedulingOptionsResolver,
    TimeProvider timeProvider) : IRoundGenerationService
{
    public async Task<Round> GenerateNextRoundIfNeededAsync(string gameKey, RoundConfig config, TimeSpan? roundDurationOverride = null, CancellationToken cancellationToken = default)
    {
        var options = roundSchedulingOptionsResolver.Resolve(gameKey);

        var latest = await roundRepository.GetLatestByGameKeyAsync(options.GameKey, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // REQ-205: this scheduler job is the only production-scheduled
        // trigger point Tier 0 has (each GameKey's own round-generation
        // cron — generate-round.yml until S-136/ADR-0072, now
        // generate-grid-round.yml/generate-path-round.yml), so closing
        // (and thereby locking FinalPoints/the leaderboard total for) a round
        // happens here rather than needing a second scheduled job of its own.
        //
        // The round to close is never "latest" itself here — "latest" only
        // ever becomes the round about to start (or already active), one
        // full cycle before it, in the same generation call that made it
        // "latest" (see the branch below: startTime = latest?.EndTime ??
        // now). By construction, that predecessor's EndTime equals latest's
        // StartTime, so once latest has actually started (checked below),
        // its predecessor has necessarily already ended and is exactly the
        // round this job has never had a chance to close until now.
        // CloseRoundAsync is idempotent (its own doc comment), so a repeat
        // call here on an already-closed predecessor is harmless.
        //
        // ADR-0102 (S-204) exception: for "xg-predict" specifically, "latest"
        // is always created with StartTime = SuggestedStartTime = its own
        // generation-time "now" — NOT `predecessor.EndTime` — so the
        // "predecessor's EndTime equals latest's StartTime" equality above no
        // longer holds for that one GameKey. This does not make the check
        // below unsafe: `previous.EndTime <= now` is still evaluated
        // explicitly (never assumed from the broken equality), so a
        // predecessor whose own matches haven't finished yet is correctly
        // left open rather than closed early — it just means xg-predict's
        // predecessor may take one additional cron cycle to close relative
        // to xg-grid/xg-path's guaranteed-immediate case described above.
        if (latest is not null && latest.StartTime <= now)
        {
            var previous = await roundRepository.GetPreviousByGameKeyAsync(options.GameKey, latest.StartTime, cancellationToken);
            if (previous is not null && previous.EndTime <= now)
                await roundCloseService.CloseRoundAsync(previous.Id, now, cancellationToken);
        }

        // An upcoming (not-yet-started) round already exists for this game —
        // that already satisfies "one round ahead"; generating another would
        // put the schedule two rounds ahead instead of one.
        if (latest is not null && latest.StartTime > now)
            return latest;

        var gameModule = gameModuleResolver.Resolve(options.GameKey);
        // ADR-0102: threads the existing GameKey's own latest GameInstanceId
        // (if any) through to the module, so it can decide for itself
        // whether a new instance is actually due (e.g. xG Predict comparing
        // fixture sets) rather than RoundGenerationService assuming
        // "generation always produces a new instance," which was only ever
        // true for xg-grid/xg-path's arbitrary (non-real-world) content.
        config.LatestGameInstanceId = latest?.GameInstanceId;

        // Games.XGGrid: assemble the instance and return its ID first —
        // Core.Rounds only creates the Round once generation has actually
        // succeeded (architecture-document.md §6.1's flow).
        var instance = await gameModule.GenerateInstanceAsync(config, cancellationToken);

        // ADR-0102: null means "no new round due for this GameKey right
        // now" (e.g. xG Predict determined the next real matchday hasn't
        // changed since `latest`) — treated exactly like the "one round
        // ahead already satisfied" no-op above: return `latest` unchanged,
        // persist nothing new. A module violating its own contract (
        // returning null with no `latest` to fall back to, i.e. for a
        // GameKey's first-ever round) is a bug in that module, not a
        // recoverable state here — fail loudly rather than let `latest!`
        // below throw a confusing NullReferenceException.
        if (instance is null)
        {
            if (latest is null)
            {
                throw new InvalidOperationException(
                    $"IGameModule.GenerateInstanceAsync returned null for GameKey '{options.GameKey}' with no " +
                    "existing round to fall back to — a module must only return null when " +
                    "RoundConfig.LatestGameInstanceId was non-null (see ADR-0102).");
            }

            return latest;
        }

        var startTime = instance.SuggestedStartTime ?? latest?.EndTime ?? now;

        // REQ-304: MAX(SequenceNumber)+1 scoped to this GameKey, starting at
        // 1 for a GameKey's first-ever round — read here, immediately before
        // AddAsync's own SaveChangesAsync, the same "no scheduled work
        // between the check and the write" reasoning the "one round ahead"
        // check above already relies on. The (GameKey, SequenceNumber)
        // unique index (XGArcadeDbContext) is the actual race guard: two
        // concurrent calls racing this read would otherwise compute the same
        // next value, and the loser's AddAsync fails on that constraint
        // instead of silently persisting a duplicate.
        var maxSequenceNumber = await roundRepository.GetMaxSequenceNumberByGameKeyAsync(options.GameKey, cancellationToken);

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = options.GameKey,
            GameInstanceId = instance.Id,
            SequenceNumber = (maxSequenceNumber ?? 0) + 1,
            StartTime = startTime,
            // ADR-0102: instance.SuggestedEndTime, when the module supplied
            // one, wins over chain-math entirely — a module that anchors its
            // own EndTime to real-world content timing (e.g. xG Predict's
            // last-kickoff-plus-typical-duration) knows better than a
            // fixed-period formula ever could. Falls back to the original
            // chain-math EndTime (startTime + RoundDuration) for xg-grid/
            // xg-path, unchanged. roundDurationOverride, when supplied, wins
            // over the *configured* RoundDuration for that fallback only —
            // it never mutates the shared RoundSchedulingOptions singleton
            // (IRoundGenerationService's own doc comment).
            EndTime = instance.SuggestedEndTime ?? (startTime + (roundDurationOverride ?? options.RoundDuration)),
            AllowGuessChange = options.AllowGuessChange,
        };

        return await roundRepository.AddAsync(round, cancellationToken);
    }
}
