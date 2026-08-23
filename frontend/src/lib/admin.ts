import type {
  AdminAccountMetrics,
  AdminActiveRound,
  AdminRound,
  AdminXGPathCycleState,
  ApprovePlayerDataResponse,
  ClearGuestAccountsResponse,
  CommitPlayerDataPayload,
  CommitPlayerDataResult,
  GuestAccountCountResponse,
  PendingSuggestion,
  PlayerOverride,
  RemovePlayerDataResponse,
  UnverifiedPlayerData,
  WikidataPlayerLookupResult,
} from './types';
import { ApiError, apiRequest } from './apiClient';

// REQ-503 (SCREEN-04): always registered, regardless of environment — no
// 404-as-hidden handling needed here the way the round-control probe below
// has, since this section is never Production-gated.
export async function fetchUnverifiedPlayerData(
  accessToken: string,
): Promise<UnverifiedPlayerData[]> {
  return apiRequest<UnverifiedPlayerData[]>(accessToken, '/admin/player-data/unverified');
}

// REQ-503 (2026-07-20 extension): the bulk "approve" action — a single id
// is just the N=1 case, same endpoint. Always resolves (never throws) with
// a 200 and one result per requested id; a row that no longer exists or is
// no longer unverified fails independently of the rest of the batch
// (surfaced per-row via each result's `failureReason`), never as an
// all-or-nothing batch success/failure. No `reason` field — unlike
// createPlayerOverride below, approve doesn't require one.
export async function approvePlayerData(
  accessToken: string,
  playerDataIds: string[],
): Promise<ApprovePlayerDataResponse> {
  return apiRequest<ApprovePlayerDataResponse>(accessToken, '/admin/player-data/approve', {
    method: 'POST',
    body: JSON.stringify({ playerDataIds }),
  });
}

// REQ-503 (2026-07-20 extension): the bulk "remove" action — sibling to
// approvePlayerData above in every respect except the endpoint it calls: a
// single id is just the N=1 case, same endpoint. Always resolves (never
// throws) with a 200 and one result per requested id; a row that no longer
// exists fails independently of the rest of the batch (surfaced per-row via
// each result's `failureReason`), never as an all-or-nothing batch
// success/failure. No `reason` field — same as approve, unlike
// createPlayerOverride below.
export async function removePlayerData(
  accessToken: string,
  playerDataIds: string[],
): Promise<RemovePlayerDataResponse> {
  return apiRequest<RemovePlayerDataResponse>(accessToken, '/admin/player-data/remove', {
    method: 'POST',
    body: JSON.stringify({ playerDataIds }),
  });
}

// REQ-501: 409 (an override already exists for this playerId/field) is left
// to throw like any other error — the caller shows the server's own detail
// text inline rather than treating it specially, since there's no "edit an
// existing override" UI to route to instead.
export async function createPlayerOverride(
  accessToken: string,
  playerId: string,
  field: string,
  value: string,
  reason: string,
): Promise<PlayerOverride> {
  return apiRequest<PlayerOverride>(accessToken, '/admin/player-overrides', {
    method: 'POST',
    body: JSON.stringify({ playerId, field, value, reason }),
  });
}

// REQ-505: a bare 404 here (no body, same shape as any other routing miss)
// means the round-control/user-deletion feature isn't registered in this
// environment at all (ASPNETCORE_ENVIRONMENT == Production) — mirrors
// fetchCurrentRound's existing 404-as-null idiom, but the meaning here is
// "hide the section," not "empty state to render." Catches the ApiError
// apiRequest throws for the 404 rather than letting it surface, since this
// probe's whole point is telling "not registered" apart from any other
// failure.
export async function fetchActiveAdminRound(
  accessToken: string,
  gameKey: string,
): Promise<AdminActiveRound | null> {
  try {
    return await apiRequest<AdminActiveRound>(accessToken, `/admin/rounds/${gameKey}/active`);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

// REQ-505: 404 here (no active round for this game right now) is a real
// error distinct from the probe's 404-as-hidden above — left to throw.
export async function closeAdminRound(accessToken: string, gameKey: string): Promise<AdminRound> {
  return apiRequest<AdminRound>(accessToken, `/admin/rounds/${gameKey}/close`, {
    method: 'POST',
  });
}

// REQ-505: 400 problem-details ("Invalid end time") when the chosen time
// isn't after both the round's start time and now — left to throw so the
// caller can show `detail` inline.
export async function updateAdminRoundEndTime(
  accessToken: string,
  gameKey: string,
  endTimeIso: string,
): Promise<AdminRound> {
  return apiRequest<AdminRound>(accessToken, `/admin/rounds/${gameKey}/end-time`, {
    method: 'PUT',
    body: JSON.stringify({ endTime: endTimeIso }),
  });
}

export type DeleteUserResult = 'deleted' | 'not-found';

// REQ-506: a 404 (no user with this email) is a real, expected outcome the
// caller shows inline ("No user found with that email.") rather than a
// thrown error — mirrors why fetchCurrentRound treats its own 404 as data,
// not a failure, though the meaning here is "not found," not "hidden."
// Catches the ApiError apiRequest throws for the 404 rather than letting it
// surface, same reasoning as fetchActiveAdminRound above.
export async function deleteUserByEmail(
  accessToken: string,
  email: string,
): Promise<DeleteUserResult> {
  try {
    await apiRequest<void>(accessToken, `/admin/users?email=${encodeURIComponent(email)}`, {
      method: 'DELETE',
    });
    return 'deleted';
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return 'not-found';
    throw error;
  }
}

// REQ-507: always registered, in every environment (including Production) —
// no 404-as-hidden handling needed here the way fetchActiveAdminRound has for
// its own Non-Production-only probe, since this endpoint's whole point is
// live visibility into real account counts, not test-data management. A 403
// (non-admin token) is left to throw like any other admin endpoint; the
// caller decides how to degrade (AdminScreen's AccountMetricsSection hides
// itself rather than flipping the whole page to access-denied, since
// REQ-501/502/503's unverified-data fetch already owns that page-level
// decision).
export async function fetchAdminAccountMetrics(accessToken: string): Promise<AdminAccountMetrics> {
  return apiRequest<AdminAccountMetrics>(accessToken, '/admin/accounts/metrics');
}

// REQ-508 step 1: the dry-run count shown before the bulk force-clear-guests
// action's confirm step — a live count, not an estimate, of every account
// currently matching IsGuest = true. Left to throw on any failure (401/403/
// other), same as every other admin call in this file.
export async function fetchGuestAccountCount(accessToken: string): Promise<number> {
  const body = await apiRequest<GuestAccountCountResponse>(accessToken, '/admin/accounts/guests/count');
  return body.count;
}

// REQ-508 step 2: deletes every account currently matching IsGuest = true,
// each via IAccountDeletionService.DeleteAccountAsync (ADR-0038 — no second/
// raw bulk-delete path). No request body — the dry-run count from
// fetchGuestAccountCount above is a separate call, not a parameter re-sent
// here (the endpoint re-selects matching ids fresh at execution time). Always
// resolves (never throws for a partial failure) with one result per matching
// account, same per-row-outcome discipline as approvePlayerData/
// removePlayerData above.
export async function clearGuestAccounts(accessToken: string): Promise<ClearGuestAccountsResponse> {
  return apiRequest<ClearGuestAccountsResponse>(accessToken, '/admin/accounts/guests/clear', {
    method: 'POST',
  });
}

// REQ-1209/ADR-0058: always registered, in every environment (including
// Production) — mirrors fetchAdminAccountMetrics's own reasoning, this
// reads real, always-relevant operational state (REQ-1208's persisted
// xG Path cycle), not seeded/test data, so there's no 404-as-hidden probe
// the way fetchActiveAdminRound has for the Non-Production-only
// round-control feature. `hasData: false` is a normal 200 body (REQ-1209's
// "no xG Path round has ever generated yet" case) — never a thrown error.
// A 403 (non-admin token) is left to throw like every other admin call in
// this file; the caller (AdminScreen's XGPathCycleSection) decides how to
// degrade, mirroring AccountMetricsSection's own hide-not-page-wide-deny
// choice.
export async function fetchAdminXGPathCycle(accessToken: string): Promise<AdminXGPathCycleState> {
  return apiRequest<AdminXGPathCycleState>(accessToken, '/admin/xg-path/cycle');
}

// REQ-509 (S-090)/ADR-0053: the pending-suggestion queue for
// SuggestionsScreen — its own endpoint, deliberately never merged with
// fetchUnverifiedPlayerData above (see that ADR's "never a shared row shape"
// rule). Always registered, same as every other admin call in this file; a
// 403 (non-admin token) is left to throw like every other admin endpoint.
export async function fetchPendingSuggestions(accessToken: string): Promise<PendingSuggestion[]> {
  return apiRequest<PendingSuggestion[]>(accessToken, '/admin/suggestions');
}

// REQ-509: triggers a fresh, admin-initiated Wikidata lookup for one pending
// suggestion's own player name (the suggestion, not any caller-supplied
// name, is the source of truth for which name gets looked up — this call
// takes no name parameter). A 409 (another admin already resolved this
// suggestion) and a 503 (ADR-0046's "lookup unavailable, try again" —
// distinct from a normal `found: false` no-match result) are both left to
// throw as an ApiError so the caller (SuggestionsScreen's review panel) can
// branch on `error.status` and render each as its own distinct state, never
// conflated with one another or with a generic failure.
export async function lookupSuggestionPlayer(
  accessToken: string,
  suggestionId: string,
): Promise<WikidataPlayerLookupResult> {
  return apiRequest<WikidataPlayerLookupResult>(
    accessToken,
    `/admin/suggestions/${suggestionId}/lookup`,
    { method: 'POST' },
  );
}

// REQ-509: commits the admin's reviewed/confirmed values for one pending
// suggestion — writes only through PlayerAttribute/PlayerOverride
// (ADR-0007/ADR-0053: never PlayerNameIndex) and moves the suggestion's own
// state to Committed server-side. A 400 (missing wikidataQid/fullName/reason,
// or neither nationality nor clubs provided) and a 409 (already resolved) are
// both left to throw so the caller shows the server's own detail text
// inline, same convention as createPlayerOverride above.
export async function commitSuggestion(
  accessToken: string,
  suggestionId: string,
  payload: CommitPlayerDataPayload,
): Promise<CommitPlayerDataResult> {
  return apiRequest<CommitPlayerDataResult>(
    accessToken,
    `/admin/suggestions/${suggestionId}/commit`,
    { method: 'POST', body: JSON.stringify(payload) },
  );
}

// REQ-509: rejects one pending suggestion — no request body (mirrors
// approvePlayerData/removePlayerData's own "no reason field" precedent for
// a non-Correct admin action), no PlayerAttribute/PlayerOverride/
// PlayerNameIndex write of any kind. Success is 204 No Content. A 409
// (already resolved by another admin) is left to throw, same as every other
// suggestion-review call above.
export async function rejectSuggestion(accessToken: string, suggestionId: string): Promise<void> {
  await apiRequest<void>(accessToken, `/admin/suggestions/${suggestionId}/reject`, {
    method: 'POST',
  });
}

// REQ-510/ADR-0053: the standalone variant of lookupSuggestionPlayer above —
// identical live-fetch mechanism, but keyed on a name the admin types
// directly rather than an existing suggestion's own PlayerName, and touches
// no PlayerSuggestion row at all. A 400 (blank playerName) is left to throw;
// the caller (ManualSearchSection) also disables the search action
// client-side on an empty/whitespace-only name, so this is defense in depth,
// not the primary guard.
export async function lookupPlayerByName(
  accessToken: string,
  playerName: string,
): Promise<WikidataPlayerLookupResult> {
  return apiRequest<WikidataPlayerLookupResult>(accessToken, '/admin/player-search/lookup', {
    method: 'POST',
    body: JSON.stringify({ playerName }),
  });
}

// REQ-510/ADR-0053: the standalone variant of commitSuggestion above —
// identical write path (PlayerAttribute/PlayerOverride only, same 400/omit
// validation), but never reads, creates, or touches a PlayerSuggestion row.
export async function commitPlayerSearch(
  accessToken: string,
  payload: CommitPlayerDataPayload,
): Promise<CommitPlayerDataResult> {
  return apiRequest<CommitPlayerDataResult>(accessToken, '/admin/player-search/commit', {
    method: 'POST',
    body: JSON.stringify(payload),
  });
}
