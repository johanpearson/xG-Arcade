namespace XGArcade.Data.Entities;

// COMP-04 (Core.Scoring) entity — persisted here alongside every other
// entity in the single shared DbContext, same ADR-0014 precedent as
// Round/COMP-03 and User/COMP-01.
//
// CellId references a GridCell directly, coupling this Core entity to the
// xG Grid game — an accepted v1 simplification since only one game exists
// (implementation-document.md §5's own note on this shape). Generalize the
// same way ADR-0003 generalized Round if/when a second game module needs a
// different submission shape.
public class Guess
{
    public Guid Id { get; set; }

    public required Guid RoundId { get; set; }

    public Guid? UserId { get; set; }   // nullable: null after account deletion
                                          // anonymizes this row per REQ-710,
                                          // without deleting it (other users'
                                          // uniqueness scores depend on the
                                          // total guess count staying intact)

    public required Guid CellId { get; set; }

    // REQ-201's "answer" — the raw text the player typed, kept even when it
    // matched no candidate at all, so a rejected guess can still be
    // spot-checked later (REQ-211's Tier 1 trigger in MVP-SCOPE.md: "find
    // one that was actually correct, more than a rare fluke").
    public required string SubmittedName { get; set; }

    // Null when no candidate matched both of the cell's categories at all
    // (REQ-203: incorrect) — unlike implementation-document.md §5's
    // illustrative shape, this can't be a non-nullable Guid, since an
    // incorrect guess has no real player to point at.
    public Guid? PlayerAnswerId { get; set; }

    public bool IsCorrect { get; set; }
    public int AttemptCount { get; set; }               // REQ-210, capped at 2
    public double? FinalUniquenessScore { get; set; }   // null until the round closes (S-011)
    public int? FinalPoints { get; set; }                // null until the round closes (S-011)
    public DateTime CreatedAt { get; set; }

    // REQ-216/ADR-0057: the canonical name (+ optional photo) of a real, but
    // wrong, player this cell's LOCKED, FINAL-incorrect guess turned out to
    // name — set at most once, at the exact GuessSubmissionService write
    // that locks this row with IsCorrect still false, never batched or
    // updated afterward. Both stay null for every other case: a
    // still-unlocked incorrect guess (state 2, attempts remaining — this
    // REQ never applies), a correct guess (REQ-214's PlayerAnswerId/
    // Player.PhotoUrl own that case instead), or a locked-incorrect guess
    // whose SubmittedName matched no real PlayerNameIndex candidate at all
    // (ADR-0007's "no identity to show" case). Read back, never
    // re-resolved, by GET /rounds/current (RoundEndpoints) so state 4 (round
    // closed, page reload) shows the same value without a second live
    // lookup.
    public string? MatchedPlayerName { get; set; }

    // ADR-0057: independently nullable from MatchedPlayerName — a resolved
    // name with no photo (Wikidata timeout/error/genuine no-P18) is a
    // normal, silent outcome, never an error and never a fail-closed/
    // incorrect signal (there is no correctness verdict left to compute for
    // a guess already known to be wrong).
    public string? MatchedPlayerPhotoUrl { get; set; }
}
