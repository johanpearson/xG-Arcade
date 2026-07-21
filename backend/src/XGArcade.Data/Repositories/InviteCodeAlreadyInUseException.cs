namespace XGArcade.Data.Repositories;

// REQ-402: thrown by LeagueRepository.AddCustomLeagueAsync when the DB's
// unique index on League.InviteCode (XGArcadeDbContext) rejects an insert —
// the race-safety net behind LeagueService.CreateCustomLeagueAsync's own
// pre-check (ILeagueRepository.InviteCodeExistsAsync), for the window
// between that check and the insert. Same shape/purpose as
// DisplayNameAlreadyInUseException (UserRepository) for an analogous
// system-generated-value uniqueness race.
public class InviteCodeAlreadyInUseException(string inviteCode) : Exception($"Invite code '{inviteCode}' is already in use.")
{
    public string InviteCode { get; } = inviteCode;
}
