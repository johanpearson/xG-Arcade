using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Leagues;

public class LeagueService(ILeagueRepository leagueRepository, IInviteCodeGenerator inviteCodeGenerator) : ILeagueService
{
    // A random 6-character code from InviteCodeGenerator's ~887M-combination
    // alphabet makes an actual collision vanishingly unlikely at this
    // codebase's scale — this cap exists only so a systematically broken
    // generator (or, implausibly, sustained bad luck) fails loudly with a
    // clear exception instead of looping forever, never to protect against
    // expected collision volume.
    private const int MaxInviteCodeAttempts = 5;

    public async Task<League> CreateCustomLeagueAsync(string name, Guid creatorUserId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxInviteCodeAttempts; attempt++)
        {
            var inviteCode = inviteCodeGenerator.Generate();

            // Pre-check first — cheap, and the only half of this
            // collision-handling that this codebase's InMemory-provider
            // unit tests can actually exercise (see
            // UserRepositoryTests.cs's own note on why the DB-level
            // unique-index race path below isn't automatically testable).
            // The DB's own unique index (XGArcadeDbContext) is the real
            // race-safety net for the window between this check and the
            // insert, not this check itself.
            if (await leagueRepository.InviteCodeExistsAsync(inviteCode, cancellationToken))
                continue;

            var league = new League
            {
                Id = Guid.NewGuid(),
                Name = name,
                Type = LeagueTypes.Custom,
                InviteCode = inviteCode,
                CreatedByUserId = creatorUserId,
            };

            try
            {
                var created = await leagueRepository.AddCustomLeagueAsync(league, cancellationToken);

                // REQ-402: "the creator is automatically added as a
                // member" — done here, in the same call, never left to a
                // separate step a caller could skip.
                await leagueRepository.AddMembershipAsync(created.Id, creatorUserId, cancellationToken);
                return created;
            }
            catch (InviteCodeAlreadyInUseException)
            {
                // Lost a race against another creation using the same
                // just-generated code, in the narrow window after the
                // pre-check above — regenerate and retry, same as a
                // pre-check miss. Not logged here: Core services stay plain
                // libraries with no logging dependency of their own (see
                // AccountDeletionService's own comment) — a caller that
                // wants this visible can log the exception thrown below if
                // every attempt is exhausted.
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a unique invite code after {MaxInviteCodeAttempts} attempts.");
    }

    public async Task<JoinLeagueResult> JoinByInviteCodeAsync(string inviteCode, Guid userId, CancellationToken cancellationToken = default)
    {
        var league = await leagueRepository.GetByInviteCodeAsync(inviteCode, cancellationToken);
        if (league is null)
            return new JoinLeagueResult(JoinLeagueOutcome.InvalidCode, null);

        // REQ-403 doesn't specify rejoin behavior explicitly — treated as
        // an idempotent success (not a duplicate-membership error) since a
        // player re-entering a code for a league they already belong to is
        // a benign, expected action, not a mistake to reject.
        if (await leagueRepository.IsMemberAsync(league.Id, userId, cancellationToken))
            return new JoinLeagueResult(JoinLeagueOutcome.AlreadyMember, league);

        await leagueRepository.AddMembershipAsync(league.Id, userId, cancellationToken);
        return new JoinLeagueResult(JoinLeagueOutcome.Joined, league);
    }

    public async Task<IReadOnlyList<League>> GetMemberLeaguesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await leagueRepository.GetCustomLeaguesByMemberUserIdAsync(userId, cancellationToken);
}
