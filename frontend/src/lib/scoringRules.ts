// ADR-0021: xG Arcade is scored like golf — lower is better — so an
// incorrect (or unanswered) guess locks at the WORST possible score, not 0;
// 0 is reserved for the best possible (maximally unique) correct guess.
// Mirrors backend/src/XGArcade.Core/Scoring/ScoringRules.cs's
// MaxPointsPerCell — duplicated here only for display (S-033/S-041 bugfix:
// showing this value on a locked-incorrect cell and in the running total),
// never for enforcement. The server is always the source of truth for the
// actual locked FinalPoints; this constant only decides what to show before
// that lock happens.
export const MAX_POINTS_PER_CELL = 100;

// REQ-1304/ADR-0095 (S-198): xG Predict's per-component award value — the
// opposite direction from MAX_POINTS_PER_CELL above (higher-is-better, not
// golf-style; see ScoringRules.cs's own "Exception" comment right above its
// PredictPointsPerComponent constant). Mirrors backend
// ScoringRules.PredictPointsPerComponent, duplicated here only for display
// (PredictScoringExplainer.tsx), never for enforcement — the server is
// always the source of truth for a graded prediction's actual FinalPoints.
export const PREDICT_POINTS_PER_COMPONENT = 10;
