using System.Runtime.CompilerServices;

// S-187 follow-up (REQ-1203, 2026-08-29, quality-architect finding): lets
// XGArcade.DataSync.Tests unit-test PlayerCareerStintRefreshService
// .BuildNewStintsByPlayerId's internal pure reconciliation logic directly,
// without going through a full RefreshCareerStintsAsync/repository round
// trip — same "composition-root testing" convention
// docs/coding-guidelines.md documents for XGArcade.Api.Tests
// (XGArcade.Api/AssemblyInfo.cs).
[assembly: InternalsVisibleTo("XGArcade.DataSync.Tests")]
