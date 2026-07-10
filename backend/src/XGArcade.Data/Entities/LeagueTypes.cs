namespace XGArcade.Data.Entities;

// Shared between League.Type's allowed values and every call site that
// needs to refer to "the" global league — kept as one constant so the
// literal "global" is never duplicated across LeagueRepository and its
// callers.
public static class LeagueTypes
{
    public const string Global = "global";
    public const string Custom = "custom"; // Tier 1 (REQ-402) — not written anywhere yet
}
