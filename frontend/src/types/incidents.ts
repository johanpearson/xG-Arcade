// REQ-903/ADR-0064: the response to POST /incidents
// (IncidentEndpoints.SubmitIncidentReportResponse) — the created GitHub
// issue's own URL, not secret, safe to show the player as confirmation.
export interface SubmitIncidentReportResponse {
  issueUrl: string;
}

// REQ-904/ADR-0066: one open, `user-reported`-labeled GitHub issue, as
// returned by GET /admin/incident-reports (AdminIncidentReportEndpoints
// .IncidentReportIssueResponse). Not rendered as an in-app list/detail view
// (that's explicitly out of scope, ADR-0064's "no review queue" boundary) —
// carried here only because the backend response includes it at no extra
// cost; the admin UI itself only reads `AdminIncidentReportsResponse
// .openCount` and links out to GitHub.
export interface AdminIncidentReportIssue {
  number: number;
  title: string;
  url: string;
}

// REQ-904/ADR-0066: the response to GET /admin/incident-reports
// (AdminIncidentReportEndpoints.IncidentReportsResponse). `available: false`
// means no successful GitHub poll has ever happened (cold start during an
// outage, or the token was never configured) — a distinct failure/unknown
// state that must never be read or rendered as `openCount: 0`; `openCount`
// is only meaningful when `available` is true. See PlayerSuggestionsEntry's
// use of PendingSuggestion for the sibling REQ-512 badge — this type is
// deliberately not merged with it, since REQ-904 has a genuine third state
// (`available: false`) that REQ-512's simpler count-or-403-hidden shape
// doesn't need.
export interface AdminIncidentReportsResponse {
  available: boolean;
  openCount: number;
  issues: AdminIncidentReportIssue[];
}
