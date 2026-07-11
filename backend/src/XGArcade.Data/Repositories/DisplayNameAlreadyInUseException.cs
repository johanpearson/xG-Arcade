namespace XGArcade.Data.Repositories;

// REQ-701: thrown by UserRepository.AddAsync when the DB's case-insensitive
// unique index on User.NormalizedDisplayName rejects an insert — the race-
// safety net behind AuthController.Signup's own pre-check
// (IUserRepository.DisplayNameExistsAsync), for the window between that
// check and the insert.
public class DisplayNameAlreadyInUseException(string displayName) : Exception($"Display name '{displayName}' is already in use.")
{
    public string DisplayName { get; } = displayName;
}
