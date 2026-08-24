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
// Was "deliberately narrow: only what POST /users/me/avatar itself needs"
// as of S-180/ADR-0087 — that ADR's own "Follow-up" section assigned
// "resolve a stored key into a servable URL" to REQ-517/S-181 (the admin
// queue/public-visible case). S-182 needs a *narrower* version of that
// first: the owning player's own preview of their own Pending/Rejected/
// Approved submission (REQ-722's "Seeing your own status" criterion) —
// genuinely different from ADR-0087's deferred case, which was about a
// signed/public URL usable by an admin reviewer or by other players.
// DownloadAsync below streams bytes back through this backend instead
// (never a signed URL handed to the client — ADR-0013's "backend
// mediates, frontend never talks to the provider directly" convention,
// same as SupabaseAvatarStorage's own upload/delete calls), so it doesn't
// actually build the thing ADR-0087 deferred; it's a separate, smaller
// need that happens to live on the same interface. S-181's admin-queue
// view and any future "visible to other players" public case remain
// genuinely deferred, unbuilt by this addition.
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

    // REQ-722 (S-182): resolves a previously-uploaded image's storage key
    // back into servable bytes + its Content-Type, for GET
    // /users/me/avatar/{id}/image (XGArcade.Api.Avatars.AvatarEndpoints) to
    // stream to the *owning player only* — see this interface's own doc
    // comment for why this is narrower than ADR-0087's deferred "resolve a
    // key into a URL" follow-up. Returns null if the object no longer
    // exists in storage (same "missing is a valid, non-exceptional outcome"
    // shape DeleteAsync's own "already gone counts as success" contract
    // uses) rather than throwing — the caller (AvatarEndpoints) turns that
    // into a 404, same as an unknown/not-owned submission id.
    Task<AvatarImageContent?> DownloadAsync(string storageKey, CancellationToken cancellationToken = default);
}

// REQ-722 (S-182): Content is the full image body — avatar images are
// capped at AvatarEndpoints.MaxImageSizeBytes (5 MB) at upload time, small
// enough that buffering the whole thing in memory per request is fine and
// keeps this contract simple (no stream-disposal-ownership question to
// answer across the IAvatarStorage boundary). ContentType is read back from
// the storage object's own metadata rather than a new AvatarSubmission
// column (see SupabaseAvatarStorage.DownloadAsync's own comment for why).
public record AvatarImageContent(byte[] Content, string ContentType);
