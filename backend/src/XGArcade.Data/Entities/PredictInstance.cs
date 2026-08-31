using System.ComponentModel.DataAnnotations.Schema;

namespace XGArcade.Data.Entities;

// Games.XGPredict (COMP-15) entity. This Id is what Round (XGArcade.Core)
// stores as its opaque GameInstanceId — Core never references this type
// directly (ADR-0003), same precedent as GridInstance/PathInstance.
// ADR-0096 §1: Matches is an owned collection, cascade-deleted with its
// parent, same shape as PathInstance.Puzzles/GridInstance.Cells.
public class PredictInstance
{
    public Guid Id { get; set; }
    public required Guid TemplateId { get; set; }
    public List<PredictMatch> Matches { get; set; } = [];

    // REQ-1303/ADR-0096 §4 (quality-gate fix, 2026-08-31): the round-wide
    // auto-lock instant — the earliest of this instance's own matches'
    // kickoffs. Extracted here after this exact formula
    // (`Matches.Min(m => m.KickoffUtc)`) was independently re-derived at
    // three call sites (XGPredictGameModule.ScoreSubmissionAsync,
    // PredictEndpoints' GET /predict/current and POST /predict/confirm) —
    // an explicitly [NotMapped] computed property (get-only, no backing
    // column — this codebase has no prior precedent for a computed entity
    // property, so this is spelled out explicitly rather than relying on
    // EF Core's convention-based discovery to skip it) so every caller
    // reads the one formula instead of risking silent drift if it's ever
    // changed (e.g. a grace period). No new migration needed — [NotMapped]
    // properties never appear in the model/schema. Matches is always
    // populated by the time this is read (REQ-1301: exactly 5, set at
    // generation) — no empty-sequence guard needed, same assumption every
    // existing call site already made.
    [NotMapped]
    public DateTime LockInstant => Matches.Min(m => m.KickoffUtc);
}
