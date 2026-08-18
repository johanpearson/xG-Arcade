namespace XGArcade.Data.Entities;

// REQ-215/ADR-0052 (S-089): a non-guest player's assertion that a specific
// player genuinely satisfies a cell, submitted after their own guess for it
// was scored incorrect or a REQ-211 live lookup timed out
// (GuessSubmissionOutcome.LiveLookupUnavailable). A human claim to be
// reviewed later against a fresh Wikidata lookup (REQ-509/510, S-090 —
// AdminSuggestionEndpoints.cs), not a fetch result — see ADR-0052's "claim
// vs. fetch" framing for why this is its own table rather than a new row
// shape inside REQ-503's PlayerData/Confidence="unverified" queue.
//
// COMP-06 boundary (ADR-0052, reconfirmed): this entity is never read by
// any correctness-checking path (IPlayerStoreRepository.
// HasEffectiveAttributeAsync and friends) or by PlayerNameIndex/COMP-10's
// autocomplete path. Submitting one never writes PlayerAttribute,
// PlayerOverride, or PlayerNameIndex — see SuggestionEndpoints.cs. Only the
// admin commit action (REQ-509, AdminSuggestionEndpoints.cs) turns a
// committed suggestion into a PlayerOverride/PlayerAttribute write, and even
// then only through IPlayerStoreRepository's existing write path, never this
// table directly.
//
// ADR-0076 (S-144): generalized off a single game's shape. GameKey +
// nullable, per-game opaque context fields (CellId/RowCategoryType/
// ColCategoryType for "xg-grid", PathPuzzleId for "xg-path") mirror
// ADR-0003's Round.GameKey/GameInstanceId precedent — see that ADR for the
// full reasoning, including why a new game's context is a new nullable
// column here, never a game-specific foreign key.
public class PlayerSuggestion
{
    public Guid Id { get; set; }

    // ADR-0076: same vocabulary as Round.GameKey/IGameModule.GameKey
    // ("xg-grid" / "xg-path") — which per-game context field(s) below are
    // populated is determined by this value, never inferred from which
    // field happens to be non-null.
    public required string GameKey { get; set; }

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
    //
    // ADR-0076: nullable as of S-144 — populated only when GameKey ==
    // "xg-grid". Null for every "xg-path" row, which uses PathPuzzleId
    // below instead. SuggestionEndpoints.cs enforces "exactly one of
    // CellId/PathPuzzleId set, matching GameKey" at the application level;
    // there is no database constraint for it (same trade-off this ADR
    // accepts for GameKey/GameInstanceId's own cross-game genericity).
    public Guid? CellId { get; set; }

    public required Guid RoundId { get; set; }

    // Denormalized off the GridCell at submission time rather than
    // re-resolved by a join later, so a future admin review (REQ-509/S-090)
    // never needs a second query to know "what were this cell's two
    // category types" and this row stays meaningful context even if the
    // originating round/grid is long closed.
    //
    // ADR-0076: nullable as of S-144, same "xg-grid only" scoping as
    // CellId above — xG Path has no row/col category concept at all
    // (XGPathGameModule.GetCellCategoryTypesAsync's own NotSupportedException).
    public string? RowCategoryType { get; set; }
    public string? ColCategoryType { get; set; }

    // ADR-0076 (S-144): xG Path's equivalent of CellId above — the specific
    // PathPuzzle (target player) this report concerns, populated only when
    // GameKey == "xg-path". Not a second copy of the instance id (that's
    // already RoundId -> Round.GameInstanceId, per ADR-0003) — this
    // identifies which of a PathInstance's several puzzles, the same
    // structural role CellId plays for a GridInstance's several cells. No FK
    // constraint, same "opaque cross-game reference" reasoning as CellId.
    public Guid? PathPuzzleId { get; set; }

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

    // REQ-509 (S-090): "the action is logged with admin_id and a timestamp,"
    // for BOTH a commit and a reject — set exactly once, by
    // IPlayerSuggestionRepository.ResolveAsync, at the same moment Status
    // moves off Pending. Both null until then. Deliberately on this row
    // itself rather than a separate audit-log table: PlayerAttribute (the
    // club write path — see this entity's own COMP-06 boundary comment
    // above) has no audit columns of its own, so recording admin/when here
    // covers the club write, the nationality write (PlayerOverride's own
    // LockedByAdminId/LockedAt separately covers that one), AND the reject
    // path (which writes nothing else at all) under one mechanism, matching
    // REQ-503's existing "who and when, on the row itself" precedent
    // (PlayerData.ApprovedByAdminId/ApprovedAt) rather than introducing a
    // general-purpose audit-log table this codebase has deliberately avoided
    // so far.
    public Guid? ResolvedByAdminId { get; set; }
    public DateTime? ResolvedAt { get; set; }

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
