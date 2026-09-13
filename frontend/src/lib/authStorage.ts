// Neutral home for the access-token localStorage key, shared by useSession.ts
// and useAppNavigation.ts. Split out so neither hook file imports from the
// other: useSession.ts owns the session lifecycle (refresh, logout, fetching
// the current user) and useAppNavigation.ts only reads this one key to decide
// initial routing (see that file's top-of-file comment) — putting the
// constant in either hook file would create a real useAppNavigation ->
// useSession (or reverse) dependency edge that shouldn't exist. See
// docs/backlog.md S-235 follow-up for why this module exists.
export const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';
