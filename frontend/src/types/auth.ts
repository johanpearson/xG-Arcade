export interface SignupResponse {
  id: string;
  email: string;
  displayName: string;
}

export interface LoginResponse {
  accessToken: string;
  refreshToken: string | null;
}

// REQ-504: GET /auth/me — `isAdmin` is the only signal the frontend has for
// whether to show the admin nav entry point at all (App.tsx); the actual
// authorization is always re-checked server-side per request regardless.
// REQ-717/ADR-0036: `email` is nullable — a guest account (`User.IsGuest`
// on the backend) has none until it claims a real one via POST /auth/claim
// (see `claimAccount` in lib/auth.ts). `isGuest` mirrors `User.IsGuest`
// directly (backend follow-up landed alongside this) — a first-class
// field, not derived from `email === null`.
export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string;
  emailConfirmed: boolean;
  isAdmin: boolean;
  isGuest: boolean;
}

// REQ-714: PUT /auth/display-name's response shape
// (AuthController.UpdateDisplayName / UpdateDisplayNameResponse).
export interface UpdateDisplayNameResponse {
  id: string;
  displayName: string;
}

// REQ-711/REQ-713: GET /auth/export's response shape
// (AuthController.Export / DataExportResponse and its nested records,
// backend/src/XGArcade.Api/Auth/AuthController.cs). Mirrors the backend's
// camelCase System.Text.Json serialization exactly, same convention as
// every other type in this file.
export interface DataExportAccountInfo {
  id: string;
  authProviderUserId: string;
  email: string | null;
  displayName: string;
  emailConfirmed: boolean;
  isGuest: boolean;
  claimedAt: string | null;
  createdAt: string;
  lastActiveAt: string;
}

export interface DataExportGuess {
  id: string;
  roundId: string;
  cellId: string;
  submittedName: string;
  playerAnswerId: string | null;
  isCorrect: boolean;
  attemptCount: number;
  finalUniquenessScore: number | null;
  finalPoints: number | null;
  createdAt: string;
  matchedPlayerName: string | null;
  matchedPlayerPhotoUrl: string | null;
}

export interface DataExportLeagueMembership {
  leagueId: string;
  leagueName: string;
  leagueType: string;
}

// `notificationPreferences` is always `null` for now — `NotificationPreference`
// doesn't exist yet (Tier 1, MVP-SCOPE.md), the same no-op-until-built
// precedent REQ-710/S-025 already established for the same table. Typed as
// `unknown | null` rather than inventing a shape that doesn't exist on the
// backend yet (the backend field itself is `object?`) — widen this once the
// table and a real DTO exist.
export interface DataExportResponse {
  account: DataExportAccountInfo;
  guesses: DataExportGuess[];
  leagueMemberships: DataExportLeagueMembership[];
  notificationPreferences: unknown | null;
}
