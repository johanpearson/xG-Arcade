// REQ-903 (ADR-0064): centralizes the incident-report entry point's
// player-facing copy in one place — same discipline suggestionCopy.ts
// already established for REQ-215's own entry point.

// Shown alongside the entry point for a guest account — present but
// disabled/inert, never hidden (REQ-903's "advertised, not hidden" guest
// rule, mirroring REQ-215's precedent). Points at Settings' existing claim
// path (SCREEN-08). No longer says "above" (2026-08-10 footer relocation) —
// the entry point isn't next to the claim section anymore, it's reachable
// from anywhere.
export const INCIDENT_REPORT_GUEST_LOCKED_COPY =
  'Register for a full account (Settings → Save your progress) to report a problem here.';

// Shown once a report submits successfully, alongside the created issue's
// (non-secret) URL.
export const INCIDENT_REPORT_SUBMITTED_COPY = 'Thanks — your report was filed.';

// Title field placeholder — a short example, not a full sentence, since
// Description below is where the detail belongs.
export const INCIDENT_REPORT_TITLE_PLACEHOLDER = 'Short summary, e.g. "Grid freezes after guess submit"';

// Description placeholder — concrete example steps/expected-vs-actual
// wording, requested directly (2026-08-10) alongside the structured-fields
// change (Title/Screen split out so this field's job narrows to "what
// happened," not "everything"). Not real event data, just guidance on the
// level of detail that's useful; disappears the moment the player starts
// typing, same as any placeholder.
export const INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER =
  'Steps to reproduce, if you can — and what you expected vs. what actually happened. e.g. ' +
  '"1. Open France × Arsenal. 2. Submit a guess. 3. The grid freezes and never shows a result. ' +
  'I expected to see whether the guess was correct."';

// REQ-903 (2026-08-10 structured-fields addition, requested directly):
// Screen is a mandatory dropdown, not free text, so every issue reports a
// consistent, recognizable value rather than however a player might type
// "the grid" / "grid screen" / "home page." Mirrors App.tsx's own Screen
// union one-for-one (kept as a parallel plain-string list here, not an
// import from App.tsx, to avoid a circular dependency — App.tsx is the one
// that renders IncidentReportDialog). 'other' is the deliberate escape
// hatch for a report that isn't tied to one specific screen.
export const INCIDENT_REPORT_SCREEN_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'game-select', label: 'Choose a game' },
  { value: 'grid', label: 'xG Grid' },
  { value: 'path', label: 'xG Path' },
  { value: 'leaderboard', label: 'Leaderboard' },
  { value: 'leagues', label: 'Leagues' },
  { value: 'settings', label: 'Settings' },
  { value: 'admin', label: 'Admin' },
  { value: 'admin-suggestions', label: 'Admin — Player suggestions' },
  { value: 'other', label: "Something else / not sure" },
];

export const INCIDENT_REPORT_DEFAULT_SCREEN_OPTION = 'other';
