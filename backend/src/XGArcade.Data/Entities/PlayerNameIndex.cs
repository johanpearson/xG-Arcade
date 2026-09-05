namespace XGArcade.Data.Entities;

// COMP-10 (Data.PlayerNameIndex) — see ADR-0007 and architecture-document.md
// boundary rule 5. Broad, bulk-imported (PlayerNameIndexImporter), refreshed
// periodically as a whole, deliberately separate from PlayerAttribute/
// PlayerOverride (COMP-06): used ONLY for autocomplete suggestions, never
// for correctness-checking. A PlayerNameIndex row existing for a player with
// zero PlayerAttribute rows is a normal, expected state, not a bug.
public class PlayerNameIndex
{
    // A synthetic key local to PlayerNameIndex/COMP-10 — a deterministic hash
    // of the Wikidata QID (see PlayerNameIndexImporter.DeterministicPlayerId),
    // NOT the same id space as Player.Id (COMP-06), which is a plain
    // Guid.NewGuid() with no relationship to the QID (WikidataLookupService).
    // For the same real person these two GUIDs will practically always
    // differ, and nothing today reconciles them — there is no lookup that
    // maps a PlayerNameIndex row to its corresponding Player/PlayerAttribute
    // row for the same person. If a future story (e.g. REQ-208's name
    // resolution) ever needs that reconciliation, it must be built
    // deliberately; do not assume or wire up an implicit relationship
    // between the two id spaces — see ADR-0007 and the note this correction
    // itself responds to (S-032 quality-gate review, 2026-07-17).
    public Guid PlayerId { get; set; }

    public required string PrimaryName { get; set; }

    // Lowercased, diacritics/punctuation stripped — PlayerNameNormalizer.Normalize,
    // reused rather than reimplemented (REQ-208's shared normalize()).
    public required string NormalizedName { get; set; }

    public int? BirthYear { get; set; }
    public string? PrimaryNationality { get; set; }

    // No PhotoUrl: dropped 2026-07-18 (RemovePlayerNameIndexPhotoUrl
    // migration) — the autocomplete contract never exposed a photo
    // (design-document.md's SCREEN-02 note records the avatar as not
    // shippable for exactly that reason), so fetching/storing P18 was dead
    // weight in the bulk import. Re-add deliberately if a real photo
    // feature ever exists.

    // Bug fix (2026-09-05, ADR-0107): a real, reported incident — two
    // genuinely different real footballers both named "Jonas Olsson"
    // (Wikidata QIDs for different people) both got indexed here from a
    // routine nationality-pool sweep, so xG Connect's candidate/target-pick
    // resolution (which only ever had a NAME to go on — see
    // ConnectChainStepService.SubmitChainStepAsync's own "known, deliberate
    // simplification" comment) had no way to tell them apart and
    // deterministically picked the wrong one, permanently rejecting every
    // genuinely correct connection through the right one. This is the
    // deliberate reconciliation this entity's own PlayerId doc comment
    // above says "must be built deliberately" if ever needed — added as its
    // OWN new column rather than by changing what PlayerId means, so
    // PlayerId's existing "different id space than Player.Id" contract is
    // completely unchanged. Nullable: a row indexed before this column
    // existed has no value until the next `import-player-name-index` run
    // backfills it (PlayerNameIndexImporter.ToIndexEntry always populates
    // it for every row going forward) — callers that need to resolve a
    // specific real person unambiguously (xG Connect) must treat a null
    // value here as "this suggestion can't yet be disambiguated by id,"
    // never crash on it. See ADR-0107 for the full decision.
    public string? WikidataQid { get; set; }
}
