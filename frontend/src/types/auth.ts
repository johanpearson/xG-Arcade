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
