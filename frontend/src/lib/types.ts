// Shapes mirror the API DTOs exactly (see backend/src/XGArcade.Api/Rounds/RoundEndpoints.cs
// and Guesses/GuessEndpoints.cs) — kept as plain types at the boundary per
// coding-guidelines.md's "props explicitly typed, never any."
//
// S-239: this file used to hold every type/interface directly; it's now a
// barrel that re-exports the domain-grouped modules under ../types/, kept
// so every existing `from '../lib/types'`/`from './types'` import site keeps
// working unchanged. Add new types to the relevant module under
// frontend/src/types/, not here — mirrors how lib/api.ts was split by
// domain in S-111.
export * from '../types/shared';
export * from '../types/grid';
export * from '../types/path';
export * from '../types/predict';
export * from '../types/higher-lower';
export * from '../types/connect';
export * from '../types/auth';
export * from '../types/leaderboard';
export * from '../types/leagues';
export * from '../types/social';
export * from '../types/users';
export * from '../types/admin';
export * from '../types/announcements';
export * from '../types/incidents';
