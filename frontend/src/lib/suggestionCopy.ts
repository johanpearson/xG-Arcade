// REQ-215 (S-089): centralizes the suggestion entry point's player-facing
// copy in one place, the same discipline guestExpiryCopy.ts already
// established for REQ-718's expiry sentence — this entry point renders at
// two independent trigger sites in GuessInput.tsx (an incorrect scored
// guess, and a REQ-211 live-lookup timeout), so without a single source the
// wording could drift between them over time.

// Shown alongside the entry point for a guest account — present but
// disabled/inert, never hidden (REQ-215's "advertised, not hidden" guest
// rule). Points at Settings' existing claim path (SCREEN-08) rather than
// inventing new copy for "how to stop being a guest."
export const SUGGESTION_GUEST_LOCKED_COPY =
  'Register for a full account (Settings → Save your progress) to suggest a correction here.';

// Shown once a suggestion submits successfully. Deliberately does not say
// or imply anything about this guess's own score changing — REQ-215's
// 2026-08-01 "no retroactive rescoring" decision means a suggestion is a
// data-correction proposal for everyone else's future guesses only, never a
// mechanism for revisiting the guess that prompted it.
export const SUGGESTION_SUBMITTED_COPY =
  "Thanks — an admin will review this. It won't change this guess's own score.";
