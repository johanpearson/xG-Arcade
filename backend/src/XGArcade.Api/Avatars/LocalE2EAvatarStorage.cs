using XGArcade.Core.Storage;

namespace XGArcade.Api.Avatars;

// Stand-in for IAvatarStorage so ci.yml's local E2E stack (no live Supabase
// project, same gap XGArcade.Api.Auth.LocalE2EAuthClient already covers for
// Supabase Auth) never tries to construct a real
// XGArcade.Storage.Supabase.SupabaseAvatarStorage — that registration reads
// Supabase:Url/ServiceRoleKey, neither of which ci.yml's e2e-tests job
// configures. Only ever wired up when AuthSetup.IsLocalE2EAuth is true —
// see ServiceRegistration.AddAvatarStorageServices, the same gating
// AuthSetup.ConfigureSupabaseAuthentication already applies to
// LocalE2EAuthClient. No E2E spec exercises POST /users/me/avatar yet (no
// frontend upload UI — S-182), so this is never actually invoked today;
// it exists so the app can still start in local-e2e mode without a real
// Supabase project, the same reason LocalE2EAuthClient exists.
//
// XGArcade.Api.Tests' WebApplicationFactory-based tests never rely on this
// class either — they swap IAvatarStorage for a hand-rolled fake directly
// (docs/coding-guidelines.md's no-mocking-framework rule), the same
// Fake*/RemoveAll<T> pattern IGitHubIssueClient's own tests already use.
internal sealed class LocalE2EAvatarStorage : IAvatarStorage
{
    public Task<string> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(Guid.NewGuid().ToString("N"));

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
