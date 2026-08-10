namespace XGArcade.DataSync.Wikidata;

// S-100 (docs/backlog.md): the category axis IntersectionQuerySpecs.ByCategoryPair
// keys on -- a client-local enumeration, distinct from Games.XGGrid's
// CategoryPairingRules string vocabulary (Core/DataSync never reference a
// game-specific type, ADR-0003). Only the three values S-100 actually
// migrates onto the spec table; S-101 extends this enum with Trophy/
// TeamTrophy when it migrates the remaining six trophy-involving pairs.
internal enum CategoryType
{
    Country,
    Club,
    NationalTeam,
}
