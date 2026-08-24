namespace XGArcade.Core.Storage;

// REQ-722/ADR-0087 (S-180): the contract POST /users/me/avatar
// (XGArcade.Api.Avatars.AvatarEndpoints) uses to persist an uploaded avatar
// image. Kept in XGArcade.Core so it can be injected via DI without
// XGArcade.Api or XGArcade.Core knowing this is backed by Supabase Storage
// — the concrete implementation (XGArcade.Storage.Supabase.
// SupabaseAvatarStorage) lives in its own project, XGArcade.Storage, never
// here and never in XGArcade.Api, per ADR-0004's hosting-agnostic boundary.
// See ADR-0087's "For AI agents" section for the full placement reasoning,
// including why this deliberately does NOT reuse ISupabaseAuthClient's
// existing (but not-to-be-copied-uncritically) placement directly inside
// XGArcade.Core/Auth.
//
// Deliberately narrow: only what POST /users/me/avatar itself needs (store
// a new image, best-effort delete a superseded one). Resolving a stored
// key into something servable (a signed/public URL) is REQ-517/S-181's
// future admin-approval-flow concern — its own addition to this interface
// when that story is built, not pre-built speculatively here.
public interface IAvatarStorage
{
    // Uploads image content and returns the storage key (object path) it
    // was stored under — never the raw bytes, and never a full URL (see
    // this interface's own doc comment for why resolving a key to a URL is
    // deliberately left to a later story). contentType is passed through
    // as-is to the storage provider's own Content-Type handling.
    Task<string> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    // Best-effort delete of a previously-uploaded image, by the storage key
    // UploadAsync returned — used when a Pending submission is replaced
    // (REQ-722) to avoid leaving an orphaned object behind. Returns true
    // when the object no longer exists afterward, including when it was
    // already gone before this call (same "already gone counts as success"
    // contract XGArcade.Core.Auth.ISupabaseAuthClient.DeleteUserAsync uses
    // for REQ-710) — never throws for a not-found key.
    Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
