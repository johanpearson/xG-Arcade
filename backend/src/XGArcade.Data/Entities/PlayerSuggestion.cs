namespace XGArcade.Data.Entities;

// REQ-215/ADR-0052 (S-089): a non-guest player's assertion that a specific
// player genuinely satisfies a cell, submitted after their own guess for it
// was scored incorrect or a REQ-211 live lookup timed out
// (GuessSubmissionOutcome.LiveLookupUnavailable). A human claim to be
// reviewed later against a fresh Wikidata lookup (REQ-509, S-090 — not
// built yet), not a fetch result — see ADR-0052's "claim vs. fetch" framing
// for why this is its own table rather than a new row shape inside
// REQ-503's PlayerData/Confidence="unverified" queue.
//
// COMP-06 boundary (ADR-0052, reconfirmed): this entity is never read by
// any correctness-checking path (IPlayerStoreRepository.
// HasEffectiveAttributeAsync and friends) or by PlayerNameIndex/COMP-10's
// autocomplete path. Submitting one never writes PlayerAttribute,
// PlayerOverride, or PlayerNameIndex — see SuggestionEndpoints.cs. Only a
// future admin commit action (REQ-509) may turn an approved suggestion into
// a PlayerOverride/PlayerAttribute write, and even then only through
// IPlayerStoreRepository's existing write path, never this table directly.
public class PlayerSuggestion
{
    public Guid Id { get; set; }

    // The player name exactly as typed in the guess that triggered this
    // suggestion (Guess.SubmittedName) — already known, not re-entered by
    // the submitting player.
    public required string PlayerName { get; set; }

    // A single asserted nationality — unlike AssertedClubs below, REQ-215's
    // acceptance criteria treats nationality as one value per suggestion,
    // matching how a cell's own "nationality" category (PlayerAttribute.
    // AttributeType) is always a single value per player.
    public required string AssertedNationality { get; set; }

    public required Guid SubmittingUserId { get; set; }

    // Deliberately no FK constraint to Users: User rows are hard-deleted on
    // account deletion (UserRepository.DeleteAsync/REQ-710), and this story
    // doesn't define anonymize-on-delete semantics for PlayerSuggestion the
    // way Guess.UserId already has one (REQ-710's null-out-don't-delete
    // rule) — leaving this unconstrained avoids silently blocking account
    // deletion behind a suggestion row, same "no FK" choice Guess.UserId
    // itself already makes for the identical reason.

    // The originating GridCell (Games.XGGrid/COMP-05) this suggestion was
    // triggered from. Coupling this table to a game-specific entity is the
    // same accepted v1 simplification Guess.CellId already documents — see
    // that entity's own comment. No FK constraint for the same reason
    // Guess.CellId has none: an opaque cross-game reference, not enforced
    // referential integrity.
    public required Guid CellId { get; set; }

    public required Guid RoundId { get; set; }

    // Denormalized off the GridCell at submission time rather than
    // re-resolved by a join later, so a future admin review (REQ-509/S-090)
    // never needs a second query to know "what were this cell's two
    // category types" and this row stays meaningful context even if the
    // originating round/grid is long closed.
    public required string RowCategoryType { get; set; }
    public required string ColCategoryType { get; set; }

    // REQ-215: never anything but Pending as of this story (S-089) — no
    // code path here writes Committed/Rejected. Modeled as an enum now
    // (plain EF Core default int-column mapping, no HasConversion — no
    // existing precedent in this codebase for storing an enum as a string,
    // see PlayerData.Confidence's plain-string convention for contrast)
    // because REQ-509/S-090's admin review/commit endpoints need those two
    // additional values, and retrofitting an enum after real Pending rows
    // already exist is avoidable churn.
    public PlayerSuggestionStatus Status { get; set; } = PlayerSuggestionStatus.Pending;

    public DateTime CreatedAt { get; set; }

    // REQ-215: "at least one club" — a separate child table (one row per
    // asserted club), not a delimited/JSON column. Mirrors the owned-
    // collection shape already used for GridInstance.Cells/PathInstance.
    // Puzzles rather than introducing this codebase's first multi-valued
    // column.
    public List<PlayerSuggestionClub> AssertedClubs { get; set; } = [];
}

public enum PlayerSuggestionStatus
{
    Pending,
    Committed,
    Rejected,
}
