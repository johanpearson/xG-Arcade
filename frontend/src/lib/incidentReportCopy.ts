// REQ-903 (ADR-0064): centralizes the incident-report entry point's
// player-facing copy in one place — same discipline suggestionCopy.ts
// already established for REQ-215's own entry point.

// Shown alongside the entry point for a guest account — present but
// disabled/inert, never hidden (REQ-903's "advertised, not hidden" guest
// rule, mirroring REQ-215's precedent). Points at Settings' existing claim
// path (SCREEN-08), same as SUGGESTION_GUEST_LOCKED_COPY.
export const INCIDENT_REPORT_GUEST_LOCKED_COPY =
  'Register for a full account (Save your progress, above) to report a problem here.';

// Shown once a report submits successfully, alongside the created issue's
// (non-secret) URL.
export const INCIDENT_REPORT_SUBMITTED_COPY = 'Thanks — your report was filed.';

// Textarea placeholder — concrete examples of a good report, requested
// directly (2026-08-10) alongside moving this entry point into the footer.
// Not real event data, just guidance on the level of detail that's useful
// (what you did, what you expected, what happened instead); disappears the
// moment the player starts typing, same as any placeholder.
export const INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER =
  'What happened, and what did you expect instead? e.g. "The grid froze after I ' +
  'submitted a guess for Brazil × Arsenal" or "My score didn\'t update after the round closed."';
