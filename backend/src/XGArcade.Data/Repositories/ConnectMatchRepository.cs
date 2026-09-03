using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class ConnectMatchRepository(XGArcadeDbContext dbContext) : IConnectMatchRepository
{
    public async Task<ConnectMatch> AddMatchAsync(ConnectMatch match, CancellationToken cancellationToken = default)
    {
        dbContext.ConnectMatches.Add(match);
        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    public async Task<ConnectMatch?> GetMatchByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectMatches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<ConnectTargetPick> AddOrUpdateTargetPickAsync(
        Guid matchId, Guid? userId, Guid targetPlayerId, DateTime selectedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it), tracked this time
        // (unlike the AsNoTracking reads elsewhere in this class) since this
        // call may update an existing row in place.
        var existing = await dbContext.ConnectTargetPicks
            .FirstOrDefaultAsync(p => p.ConnectMatchId == matchId && p.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            existing.TargetPlayerId = targetPlayerId;
            existing.SelectedAt = selectedAt;
            await dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var pick = new ConnectTargetPick
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            TargetPlayerId = targetPlayerId,
            SelectedAt = selectedAt,
        };

        dbContext.ConnectTargetPicks.Add(pick);
        await dbContext.SaveChangesAsync(cancellationToken);
        return pick;
    }

    public async Task<ConnectTargetPick?> GetTargetPickAsync(Guid matchId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectTargetPicks
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ConnectMatchId == matchId && p.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<ConnectTargetPick>> GetTargetPicksForMatchAsync(
        Guid matchId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectTargetPicks
            .AsNoTracking()
            .Where(p => p.ConnectMatchId == matchId)
            .ToListAsync(cancellationToken);

    public async Task<ConnectChainStep> AddChainStepAsync(ConnectChainStep chainStep, CancellationToken cancellationToken = default)
    {
        dbContext.ConnectChainSteps.Add(chainStep);
        await dbContext.SaveChangesAsync(cancellationToken);
        return chainStep;
    }

    // REQ-1404/S-211: load-then-save (coding-guidelines.md — never
    // ExecuteUpdateAsync), tracked (not AsNoTracking) since every row this
    // matchId resolves to is mutated in place. See IConnectMatchRepository's
    // own doc comment for why this is whole-match-scoped rather than
    // per-pick-id.
    public async Task LockTargetPicksForMatchAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        var picks = await dbContext.ConnectTargetPicks
            .Where(p => p.ConnectMatchId == matchId)
            .ToListAsync(cancellationToken);

        foreach (var pick in picks)
            pick.IsLocked = true;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // REQ-1405/S-212: the forfeit sweep's candidate set — see this method's
    // own doc comment on IConnectMatchRepository.
    public async Task<IReadOnlyList<ConnectMatch>> GetActiveMatchesPastDeadlineAsync(
        DateTime now, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectMatches
            .AsNoTracking()
            .Where(m => m.Status == ConnectMatchStatus.Active && m.DeadlineUtc != null && m.DeadlineUtc <= now)
            .ToListAsync(cancellationToken);

    // REQ-1405/S-212: load-then-save (coding-guidelines.md — never
    // ExecuteUpdateAsync), tracked since this row is mutated in place.
    public async Task<ConnectMatch> StartMatchAsync(
        Guid matchId, DateTime startedAt, DateTime deadlineUtc, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.ConnectMatches.FirstAsync(m => m.Id == matchId, cancellationToken);

        match.Status = ConnectMatchStatus.Active;
        match.StartedAt = startedAt;
        match.DeadlineUtc = deadlineUtc;

        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    // REQ-1405/S-212: load-then-save, tracked. `??=` is what makes this
    // idempotent — a slot already marked timed-out keeps its original
    // timestamp even if a later sweep pass calls this again for the same
    // slot (see IConnectMatchRepository's own doc comment).
    public async Task<ConnectMatch> MarkPlayerTimedOutAsync(
        Guid matchId, bool isPlayerA, DateTime timedOutAt, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.ConnectMatches.FirstAsync(m => m.Id == matchId, cancellationToken);

        if (isPlayerA)
            match.PlayerATimedOutAt ??= timedOutAt;
        else
            match.PlayerBTimedOutAt ??= timedOutAt;

        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    // REQ-1407/S-214: load-then-save, tracked. `??=` is what makes this
    // idempotent — same shape as MarkPlayerTimedOutAsync immediately above.
    public async Task<ConnectMatch> MarkPlayerBustedAsync(
        Guid matchId, bool isPlayerA, DateTime bustedAt, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.ConnectMatches.FirstAsync(m => m.Id == matchId, cancellationToken);

        if (isPlayerA)
            match.PlayerABustedAt ??= bustedAt;
        else
            match.PlayerBBustedAt ??= bustedAt;

        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    // REQ-1405/1408/1409/S-212/S-214: load-then-save, tracked. Scores are
    // written in this same call — see this method's own doc comment on
    // IConnectMatchRepository.
    public async Task<ConnectMatch> ResolveMatchAsync(
        Guid matchId, ConnectMatchOutcome outcome, DateTime resolvedAt,
        int? playerAScore, int? playerBScore, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.ConnectMatches.FirstAsync(m => m.Id == matchId, cancellationToken);

        match.Status = ConnectMatchStatus.Resolved;
        match.Outcome = outcome;
        match.ResolvedAt = resolvedAt;
        match.PlayerAScore = playerAScore;
        match.PlayerBScore = playerBScore;

        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    public async Task<IReadOnlyList<ConnectChainStep>> GetChainStepsForMatchAndUserAsync(
        Guid matchId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectChainSteps
            .AsNoTracking()
            .Where(s => s.ConnectMatchId == matchId && s.UserId == userId)
            .ToListAsync(cancellationToken);

    // REQ-710/ADR-0101: load-then-save (coding-guidelines.md — never
    // ExecuteUpdateAsync, the InMemory test provider can't translate it),
    // tracked (not AsNoTracking) since every row here is mutated in place.
    // Three separate queries/loops rather than one combined LINQ query
    // across entity types, mirroring
    // PredictInstanceRepository.AnonymizePredictionsByUserIdAsync's own
    // one-entity-type-at-a-time shape.
    public async Task AnonymizeUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var matches = await dbContext.ConnectMatches
            .Where(m => m.PlayerAUserId == userId || m.PlayerBUserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var match in matches)
        {
            if (match.PlayerAUserId == userId)
                match.PlayerAUserId = null;
            if (match.PlayerBUserId == userId)
                match.PlayerBUserId = null;
        }

        var targetPicks = await dbContext.ConnectTargetPicks
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var pick in targetPicks)
        {
            pick.UserId = null;
        }

        var chainSteps = await dbContext.ConnectChainSteps
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var step in chainSteps)
        {
            step.UserId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
