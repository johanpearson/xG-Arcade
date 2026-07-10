namespace XGArcade.Core.Rounds;

// REQ-302: a Round's status is always calculated live from its start/end
// time, never stored as a separate field that could drift out of sync.
public enum RoundStatus
{
    Upcoming,
    Active,
    Closed,
}
