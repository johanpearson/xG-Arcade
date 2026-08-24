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

    // REQ-722 (S-182): no real Supabase project exists in this mode (see
    // class doc comment above), and this class never actually persists
    // UploadAsync's bytes anywhere to read back — same "unverifiable but
    // must not crash" shape LocalE2EAuthClient's stand-in methods use
    // throughout (e.g. DeleteUserAsync's unconditional success). Returns a
    // trivial, hardcoded 1x1 transparent PNG for every key rather than null,
    // so that once a frontend E2E spec exercises GET
    // /users/me/avatar/{id}/image (none does yet — no upload UI, S-182),
    // the endpoint has real, decodable image bytes to stream back instead
    // of every lookup looking like a 404 regardless of storageKey.
    public Task<AvatarImageContent?> DownloadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<AvatarImageContent?>(new AvatarImageContent(PlaceholderPngBytes, "image/png"));

    // A minimal valid 1x1 transparent PNG (base64), the smallest byte
    // sequence most image decoders/`<img>` tags will accept without error —
    // not derived from any real avatar upload, purely a decodable stand-in.
    private static readonly byte[] PlaceholderPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
