using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using XGArcade.Core.Storage;

namespace XGArcade.Storage.Supabase;

// REQ-722/ADR-0087 (S-180): the bucket this client always writes to/deletes
// from — configured via XGArcade.Api (Supabase:AvatarBucketName, ADR-0087),
// never hardcoded, following the same "own tiny DI type, not a bare
// string" shape XGArcade.Core.Auth.SupabaseServiceRoleKey already uses for
// the identical reason (typed-client activation can't resolve a bare
// string constructor parameter unambiguously).
public record SupabaseAvatarBucketOptions(string BucketName);

// Calls Supabase Storage's REST API directly (ADR-0087) — the same
// "backend mediates, frontend never talks to the provider directly"
// pattern ADR-0013 established for Supabase Auth
// (XGArcade.Core.Auth.SupabaseAuthClient), except this concrete client
// deliberately lives in its own project rather than XGArcade.Core — see
// IAvatarStorage's own doc comment and ADR-0087's "For AI agents" section
// for why.
//
// HttpClient is registered as a typed client by XGArcade.Api
// (ServiceRegistration.cs) with BaseAddress = Supabase:Url and the
// service_role key set on every request's "apikey"/Authorization headers —
// never the anon key. Every write here is a backend-initiated call to a
// bucket with no public write policy, the same reasoning
// SupabaseAuthClient.DeleteUserAsync documents for its own Admin-API-only
// service_role use (REQ-710/ADR-0026); unlike that method, though, every
// call on this class needs the elevated key, so it's set once on the
// HttpClient's own DefaultRequestHeaders at registration time rather than
// per-request.
//
// NOT independently verified against a live Supabase project from this
// sandbox (no network access to supabase.com here) — the request/response
// shapes below follow Supabase Storage's publicly documented REST API
// (object upload: POST /storage/v1/object/{bucket}/{path}; bulk delete:
// DELETE /storage/v1/object/{bucket} with a JSON {"prefixes": [...]} body;
// signed URL: POST /storage/v1/object/sign/{bucket}/{path} with a JSON
// {"expiresIn": <seconds>} body, returning {"signedURL": "/object/sign/
// {bucket}/{path}?token=..."} — a path RELATIVE to /storage/v1, per
// Supabase's documented createSignedUrl behavior, not an absolute URL),
// the same "flagged for manual verification" caveat SupabaseAuthClient.cs
// already carries throughout for its own unverified Supabase calls (e.g.
// SignInAnonymouslyAsync/LinkEmailPasswordAsync). Flagged for manual
// verification against a real Supabase project before this ships.
public class SupabaseAvatarStorage(HttpClient httpClient, SupabaseAvatarBucketOptions bucketOptions) : IAvatarStorage
{
    // REQ-517: short-lived on purpose — this is a per-request admin-queue
    // preview, never persisted or reused across requests (IAvatarStorage.
    // GetPreviewUrlAsync's own doc comment), so there's no reason to make
    // it long-lived and every reason (least-privilege exposure of an
    // otherwise-private bucket object) not to.
    private const int PreviewUrlExpirySeconds = 300;

    public async Task<string> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        // A fresh, unguessable object path per upload — never derived from
        // a caller-supplied filename (avoids path traversal/collision
        // entirely; REQ-722 has no requirement that the stored key be
        // human-readable, and AvatarSubmission.ImageStorageKey is opaque to
        // every reader of this codebase).
        var storageKey = Guid.NewGuid().ToString("N");
        var requestPath = $"storage/v1/object/{bucketOptions.BucketName}/{storageKey}";

        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await httpClient.PostAsync(requestPath, streamContent, cancellationToken);
        await EnsureSuccessAsync(response, $"upload to '{requestPath}'", cancellationToken);

        return storageKey;
    }

    public async Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var requestPath = $"storage/v1/object/{bucketOptions.BucketName}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestPath)
        {
            Content = JsonContent.Create(new { prefixes = new[] { storageKey } }),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);

        // A 404 here counts as success — same "already gone is an
        // acceptable end state" contract as
        // SupabaseAuthClient.DeleteUserAsync (REQ-710). Supabase's own bulk
        // delete endpoint is documented to return 200 with an empty result
        // list for a prefix that doesn't exist rather than a 404, so
        // IsSuccessStatusCode alone already covers that case too — the
        // explicit NotFound check is defensive, matching
        // DeleteUserAsync's own belt-and-braces shape.
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    // REQ-722 (S-182): GET storage/v1/object/{bucket}/{path} — the plain
    // download counterpart to UploadAsync's POST to the same path shape.
    // Content-Type is read back from the response's own header rather than
    // stored on AvatarSubmission (see IAvatarStorage.DownloadAsync's own
    // doc comment) — UploadAsync above already set it correctly at upload
    // time via streamContent.Headers.ContentType, and Supabase Storage is
    // documented to persist and echo back an object's Content-Type on GET,
    // so this trusts that rather than re-deriving it. NOT independently
    // verified against a live Supabase project from this sandbox — same
    // standing caveat as every other call in this class (see this file's
    // own top-of-file comment).
    public async Task<AvatarImageContent?> DownloadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var requestPath = $"storage/v1/object/{bucketOptions.BucketName}/{storageKey}";

        using var response = await httpClient.GetAsync(requestPath, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, $"download of '{requestPath}'", cancellationToken);

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // Falls back to a generic binary type only if Supabase somehow
        // omits Content-Type on the response — shouldn't happen given
        // UploadAsync always sets one, but Results.Stream (AvatarEndpoints)
        // needs a non-null string regardless.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        return new AvatarImageContent(content, contentType);
    }

    public async Task<string> GetPreviewUrlAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var requestPath = $"storage/v1/object/sign/{bucketOptions.BucketName}/{storageKey}";

        using var response = await httpClient.PostAsJsonAsync(
            requestPath, new { expiresIn = PreviewUrlExpirySeconds }, cancellationToken);
        await EnsureSuccessAsync(response, $"sign of '{requestPath}'", cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<SignedUrlResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrEmpty(payload?.SignedURL))
        {
            throw new InvalidOperationException(
                $"Supabase Storage did not return a signedURL when signing object {storageKey}.");
        }

        // payload.SignedURL is relative to /storage/v1 (see this class's
        // own doc comment) — combined with the HttpClient's own BaseAddress
        // (Supabase:Url, ServiceRegistration.cs) into the absolute URL an
        // admin's browser can load directly.
        return new Uri(httpClient.BaseAddress!, $"storage/v1{payload.SignedURL}").ToString();
    }

    // Diagnosed against a real deployment (2026-08-24): a bare
    // `response.EnsureSuccessStatusCode()` discards Supabase Storage's
    // response body on failure, so every rejection (missing bucket,
    // disallowed MIME type, over the bucket's own size limit, ...) showed
    // up in the API's logs as an indistinguishable "400 Bad Request" with
    // no way to tell which. Supabase's error responses carry the real
    // reason as JSON in the body; folding it into the thrown exception's
    // Message means AvatarEndpoints.cs's/AdminAvatarEndpoints.cs's own
    // `logger.LogError(ex, ...)` calls (docs/coding-guidelines.md: "log the
    // full exception server-side") now actually surface it, without
    // changing what's returned to the player — callers still only ever see
    // the generic Results.Problem detail, never this Message.
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operationDescription, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Supabase Storage {operationDescription} failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    private sealed record SignedUrlResponse(string? SignedURL);
}
