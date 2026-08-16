import { vi } from 'vitest';

// S-121 follow-up (quality-architect, flagged during the LeaderboardScreen
// split's review): `jsonResponse`/`routedFetch`/`row`/`defaultAllTimeRoute`
// used to be hand-rolled, byte-identical, in each of
// LeaderboardScreen.test.tsx/AllTimeLeaderboard.test.tsx/
// LiveLeaderboard.test.tsx/PastRoundsLeaderboard.test.tsx/
// WindowedLeaderboard.test.tsx — a single pre-split file's local helpers,
// copied once per new file rather than shared, when the split created the
// four new test files. Extracted here once duplicated in a fifth place;
// all five files import from this module instead of redefining their own
// copy.

export function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-406/407/408 (S-053/S-054): routes a fetch mock by URL substring so a
// single test can serve distinct responses to the all-time/live/
// past-rounds/window endpoints without caring about call order — every
// scope component fires the all-time poll on every mount regardless of
// which scope tab is active, so any test touching the scope selector needs
// a default all-time response too, not just the endpoint under test (see
// `defaultAllTimeRoute` below).
//
// Order matters: routes are tried in the order given, and
// '/leagues/global/leaderboard' (the all-time route) is a substring of
// every other scope's URL — a caller's more specific
// active-round/closed-rounds/window matchers must always be listed before
// `defaultAllTimeRoute`.
export function routedFetch(routes: Array<[string | RegExp, () => Promise<Response>]>) {
  return vi.fn().mockImplementation((input: RequestInfo | URL) => {
    const url = String(input);
    for (const [matcher, handler] of routes) {
      const matches = typeof matcher === 'string' ? url.includes(matcher) : matcher.test(url);
      if (matches) return handler();
    }
    throw new Error(`No mock route for ${url}`);
  });
}

// The all-time scope's default (empty) response — every other scope's
// tests need this alongside their own endpoint's mock, since the all-time
// poll fires on every mount regardless of which scope tab is selected (see
// `routedFetch` above). Not used by AllTimeLeaderboard.test.tsx itself,
// since that file mocks the all-time endpoint directly as the endpoint
// under test.
export const defaultAllTimeRoute: [string, () => Promise<Response>] = [
  '/leagues/global/leaderboard',
  () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
];

// Cuts down on repeating the same leaderboard-row literal across every
// scope's tests; `isRequestingUser` defaults to false since most rows in
// these tests aren't the "you" row.
export function row(rank: number, userId: string, displayName: string, totalPoints: number, isRequestingUser = false) {
  return { rank, userId, displayName, totalPoints, isRequestingUser };
}
