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
// REQ-517/S-181 (ADR-0087's own "Follow-up" note, not a new structural
// decision): GetPreviewUrlAsync below is that anticipated addition — the
// admin moderation queue (XGArcade.Api.Admin.AdminAvatarEndpoints) needs a
// way to resolve a Pending submission's ImageStorageKey into something an
// admin's browser can actually load as an <img> src.
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

    // Resolves a previously-uploaded image's storage key into a servable
    // URL an admin's browser can load directly — never a bare public URL
    // (this bucket has no public read policy any more than it has a public
    // write one, ADR-0087), always a short-lived signed URL generated
    // server-side per request, same "backend mediates, frontend never
    // talks to the provider directly" pattern the rest of this interface
    // already establishes for upload/delete. Called once per row by
    // AdminAvatarEndpoints' GET /admin/avatar-submissions (REQ-517), never
    // batched or cached — a fresh signed URL every time the queue is
    // fetched, since the queue is expected to be small and this avoids any
    // "is this signed URL still valid" staleness question entirely.
    Task<string> GetPreviewUrlAsync(string storageKey, CancellationToken cancellationToken = default);
}
