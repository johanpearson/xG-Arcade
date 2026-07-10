using XGArcade.Data.Repositories;

namespace XGArcade.Core.Leagues;

public class LeaderboardService(
    ILeagueRepository leagueRepository,
    IUserRepository userRepository,
    IGuessRepository guessRepository) : ILeaderboardService
{
    public async Task<IReadOnlyList<LeaderboardEntry>> GetGlobalLeaderboardAsync(
        Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        var memberUserIds = await leagueRepository.GetMemberUserIdsAsync(globalLeague.Id, cancellationToken);
        var members = await userRepository.GetByIdsAsync(memberUserIds, cancellationToken);
        var totalsByUserId = await guessRepository.GetTotalFinalPointsByUserIdsAsync(memberUserIds, cancellationToken);

        // REQ-404: sorted descending by total score. A member with no
        // locked FinalPoints yet (no rounds closed for them) is absent
        // from totalsByUserId — treated as 0, not omitted from the list.
        return members
            .Select(member => new LeaderboardEntry(
                member.Id,
                member.DisplayName,
                totalsByUserId.GetValueOrDefault(member.Id, 0),
                member.Id == requestingUserId))
            .OrderByDescending(entry => entry.TotalPoints)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
