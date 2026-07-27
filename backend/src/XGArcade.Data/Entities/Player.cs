namespace XGArcade.Data.Entities;

public class Player
{
    public Guid Id { get; set; }

    private string _fullName = string.Empty;

    public required string FullName
    {
        get => _fullName;
        set
        {
            _fullName = value;
            // Kept in lockstep with FullName rather than computed by each
            // caller separately — S-009's guess-time name matching (REQ-208's
            // Tier 0 "simple half": lowercase/diacritics/punctuation only, no
            // PlayerNameIndex/COMP-10, MVP-SCOPE.md) queries this column
            // directly, and every existing Player construction site (S-006's
            // WikidataLookupService, plus test seeding) already sets FullName,
            // so nothing else needs to change to keep it populated.
            NormalizedFullName = PlayerNameNormalizer.Normalize(value);
        }
    }

    public string NormalizedFullName { get; private set; } = string.Empty;

    // Dedup identity: the same player returned by two different intersection
    // queries (France×Arsenal and Brazil×Barcelona, say) must upsert into ONE
    // row, keyed on this — never insert-blindly per query. Nullable only for
    // a future non-Wikidata source (Tier 1); unique index where not null.
    public string? WikidataQid { get; set; }

    // REQ-214: Wikidata's P18 (image) property, carried through the same
    // country/club and club/club intersection queries that already resolve
    // FullName/WikidataQid (WikidataClient's Build*IntersectionQuery
    // methods) and set once at player creation
    // (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync, via
    // WikidataLookupService.PersistMatchesAsync), same as FullName never
    // being re-synced on a later lookup. Deliberately a single scalar here,
    // NOT a PlayerAttribute row: PlayerAttribute's composite key
    // (PlayerId, AttributeType, AttributeValue) holds many rows per player
    // (one per career club/nationality/trophy) — a per-player photo has no
    // natural "which row owns it" answer there, whereas Player is already
    // the single-row-per-person table (see WikidataQid's own comment).
    // Null whenever Wikidata has no P18 for this player — never an error,
    // never a placeholder/broken-image URL (REQ-214's explicit "no
    // broken-image icon" rule, enforced by simply omitting the value).
    // Unrelated to PlayerNameIndex.PhotoUrl, which was added for
    // autocomplete in S-032 and dropped 2026-07-18
    // (RemovePlayerNameIndexPhotoUrl migration, see PlayerNameIndex's own
    // comment) because the autocomplete UI never displayed it — that
    // decision is unaffected; this is a different field on a different,
    // correctness-side entity (COMP-06), read only by the cell-reveal path,
    // never by autocomplete (ADR-0007).
    public string? PhotoUrl { get; set; }

    // REQ-1207/S-082: Wikidata's P413 ("position played on team / speciality"),
    // carried through the same five intersection queries that already
    // resolve FullName/WikidataQid/PhotoUrl (WikidataClient's shared
    // BuildIntersectionQuery predicates) and set once at player creation
    // (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync), same
    // "never re-synced on a later lookup" rule as PhotoUrl above. A single
    // scalar, not a PlayerAttribute row, for the same reason PhotoUrl isn't:
    // a player has at most one position, no natural multiplicity. Null
    // whenever Wikidata has no P413 statement for this player — never an
    // error; xG Path's clue-reveal sequence (REQ-1203) renders a null
    // Position as an explicit "not available" clue, never a skipped turn.
    public string? Position { get; set; }

    // REQ-1207/S-082: derived from the P569 ("date of birth") binding those
    // same five queries already require for every matched player (ADR-0025's
    // male/born-1939-or-later pool filter) — extracting just the year, no
    // new SPARQL binding added for this field. Same set-once-at-creation
    // rule as Position/PhotoUrl above. Deliberately not a full DateOnly/
    // DateTime — REQ-1203's age/birth-year clue only ever needs the year.
    public int? BirthYear { get; set; }
}
