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
        response.EnsureSuccessStatusCode();

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

    public async Task<string> GetPreviewUrlAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var requestPath = $"storage/v1/object/sign/{bucketOptions.BucketName}/{storageKey}";

        using var response = await httpClient.PostAsJsonAsync(
            requestPath, new { expiresIn = PreviewUrlExpirySeconds }, cancellationToken);
        response.EnsureSuccessStatusCode();

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

    private sealed record SignedUrlResponse(string? SignedURL);
}
