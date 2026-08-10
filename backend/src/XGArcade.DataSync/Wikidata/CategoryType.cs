namespace XGArcade.DataSync.Wikidata;

// S-100 (docs/backlog.md): the category axis IntersectionQuerySpecs.ByCategoryPair
// keys on -- a client-local enumeration, distinct from Games.XGGrid's
// CategoryPairingRules string vocabulary (Core/DataSync never reference a
// game-specific type, ADR-0003). S-100 migrated the first three values
// (Country/Club/NationalTeam) onto the spec table; S-101 adds Trophy/
// TeamTrophy to migrate the remaining six trophy-involving pairs.
internal enum CategoryType
{
    Country,
    Club,
    NationalTeam,
    Trophy,
    TeamTrophy,
}
