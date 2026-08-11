using System.Runtime.CompilerServices;

// S-113: lets XGArcade.Api.Tests unit-test CompositionRoot/AuthSetup.cs's
// internal pure logic (IsLocalE2EAuth, GetClientIpPartitionKey) directly,
// without going through a full WebApplicationFactory host — see
// docs/coding-guidelines.md's "Composition-root testing" convention.
[assembly: InternalsVisibleTo("XGArcade.Api.Tests")]
