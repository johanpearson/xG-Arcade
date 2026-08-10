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
