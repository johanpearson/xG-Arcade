import type { AdminIncidentReportsResponse, SubmitIncidentReportResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-903/ADR-0064: files an in-app bug report — the backend turns it into
// a real GitHub issue server-side (POST /incidents, IncidentEndpoints),
// formatted into a consistent template from these separate fields rather
// than one free-text blob (2026-08-10 structured-fields addition, requested
// directly). A guest is rejected server-side with 403 ("Guest accounts
// cannot file incident reports") regardless of what the client UI shows,
// same "left to throw as an ApiError, UI already disables the entry point
// first" convention submitSuggestion (rounds.ts) already follows for its
// own guest restriction. `title`/`screen` are mandatory (IncidentReportDialog's
// own client-side checks are defense in depth, not the primary guard — the
// server re-validates both). `environment` is computed by the caller
// (IncidentReportDialog reads `window.location.origin`), never typed by the
// player. A 429 (per-user rate limit) and a 503 (GitHub API itself failed)
// are both left to throw like any other failure here — the caller shows
// the server's own detail text inline.
export async function reportIncident(
  accessToken: string,
  title: string,
  description: string,
  screen: string,
  environment?: string,
): Promise<SubmitIncidentReportResponse> {
  return apiRequest<SubmitIncidentReportResponse>(accessToken, '/incidents', {
    method: 'POST',
    body: JSON.stringify({ title, description, screen, environment }),
  });
}

// REQ-904/ADR-0066: the open-incident-report count for AdminScreen's
// "Incident reports" entry point. Always 200 when authorized — a GitHub-poll
// failure comes back as `available: false` in the body (never a thrown
// ApiError), so callers must branch on `available`, not on a catch block, to
// render REQ-904's distinct failure/unknown state. A 401/403 (no/insufficient
// session) is still left to throw like every other admin call.
export async function fetchAdminIncidentReports(accessToken: string): Promise<AdminIncidentReportsResponse> {
  return apiRequest<AdminIncidentReportsResponse>(accessToken, '/admin/incident-reports');
}
