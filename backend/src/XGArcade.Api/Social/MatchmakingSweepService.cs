using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Social;

// REQ-1403/ADR-0103, S-210: the periodic pairing/expiry sweep. Lives in
// XGArcade.Api (not Core.Social) because it orchestrates
// IMatchmakingOptInRepository (Core.Social) together with
// IConnectMatchRepository (Games.XGConnect, COMP-17) to create the
// resulting ConnectMatch — ADR-0103's "For AI agents" section forbids
// Core.Social taking a compile-time dependency on Games.XGConnect
// internals, so this cross-component write step can only live here, the
// same reasoning ChallengeEndpoints' own accept handler already applies to
// REQ-1402's single-pair case. This is the batch-shaped equivalent, driven
// by InternalMatchmakingSweepEndpoints on a cron rather than a player
// action.
public class MatchmakingSweepService(
    IMatchmakingOptInRepository matchmakingOptInRepository,
    IConnectMatchRepository connectMatchRepository,
    TimeProvider timeProvider)
{
    public async Task<MatchmakingSweepResult> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var waitingOptIns = await matchmakingOptInRepository.GetWaitingOptInsAsync(cancellationToken);

        // Step 1 (REQ-1403): expire every Waiting opt-in whose 12h window
        // has already passed, before any pairing is attempted this run —
        // an opt-in past its own deadline must never be paired just
        // because the sweep happened to catch it in the same pass.
        var expiredCount = 0;
        var stillWaiting = new List<MatchmakingOptIn>();
        foreach (var optIn in waitingOptIns)
        {
            if (optIn.ExpiresAt <= now)
            {
                await matchmakingOptInRepository.UpdateOptInStatusAsync(
                    optIn.Id, MatchmakingOptInStatus.Expired, resultingMatchId: null, cancellationToken);
                expiredCount++;
            }
            else
            {
                stillWaiting.Add(optIn);
            }
        }

        // Step 2 (REQ-1403): pair up remaining Waiting opt-ins two at a
        // time, oldest-opted-in-first. Greedy FIFO matching: each entrant is
        // paired with the earliest still-unmatched entrant belonging to a
        // DIFFERENT UserId that has not already been paired earlier in THIS
        // sweep run.
        //
        // pairedUserIds tracks every UserId already consumed by a pairing
        // this run — checked on both sides (the incoming opt-in and every
        // unmatched candidate) so that once a user is paired via one of
        // their rows, none of their other Waiting rows can ever be used as
        // — or matched to — a partner in the same run. Without this, a user
        // who opted in twice could end up in two separate ConnectMatch rows
        // (once per row) even though no single pairing ever matched them to
        // themselves; the acceptance criterion is stronger than "never
        // self-paired" — it's "never a participant in more than one
        // resulting match from one sweep run". A row belonging to an
        // already-paired user is simply left alone (added to `unmatched`
        // without ever being reconsidered as a candidate) — it stays
        // Waiting in the database exactly as it already was, never
        // silently dropped.
        var pairedUserIds = new HashSet<Guid>();
        var unmatched = new List<MatchmakingOptIn>();
        var pairedCount = 0;
        foreach (var optIn in stillWaiting.OrderBy(o => o.OptedInAt))
        {
            if (pairedUserIds.Contains(optIn.UserId))
            {
                unmatched.Add(optIn);
                continue;
            }

            var partnerIndex = unmatched.FindIndex(candidate =>
                candidate.UserId != optIn.UserId && !pairedUserIds.Contains(candidate.UserId));
            if (partnerIndex < 0)
            {
                unmatched.Add(optIn);
                continue;
            }

            var partner = unmatched[partnerIndex];
            unmatched.RemoveAt(partnerIndex);

            var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
            {
                Id = Guid.NewGuid(),
                PlayerAUserId = partner.UserId,
                PlayerBUserId = optIn.UserId,
                CreatedAt = now,
            }, cancellationToken);

            await matchmakingOptInRepository.UpdateOptInStatusAsync(
                partner.Id, MatchmakingOptInStatus.Paired, match.Id, cancellationToken);
            await matchmakingOptInRepository.UpdateOptInStatusAsync(
                optIn.Id, MatchmakingOptInStatus.Paired, match.Id, cancellationToken);

            pairedUserIds.Add(partner.UserId);
            pairedUserIds.Add(optIn.UserId);
            pairedCount += 2;
        }

        // Anyone left in `unmatched` stays Waiting untouched — never
        // dropped from the pool, per REQ-1403's own "remains waiting in the
        // pool rather than being silently dropped" acceptance criterion.
        return new MatchmakingSweepResult(Paired: pairedCount, Expired: expiredCount, StillWaiting: unmatched.Count);
    }
}

public record MatchmakingSweepResult(int Paired, int Expired, int StillWaiting);
