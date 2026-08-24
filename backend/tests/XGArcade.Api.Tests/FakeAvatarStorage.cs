using XGArcade.Core.Storage;

namespace XGArcade.Api.Tests;

// REQ-722/ADR-0087: stands in for the real SupabaseAvatarStorage in every
// API-level test — this suite must never call the real Supabase Storage
// REST API. Hand-rolled, not a mocking framework (docs/coding-guidelines.md),
// same shape as FakeGitHubIssueClient.
internal sealed class FakeAvatarStorage : IAvatarStorage
{
    public List<string> UploadedContentTypes { get; } = [];
    public List<string> DeletedStorageKeys { get; } = [];
    // REQ-517: every storage key GetPreviewUrlAsync was asked to resolve,
    // in call order — lets AdminAvatarEndpoints tests assert no N+1/extra
    // calls without depending on the (deterministic but arbitrary) URL
    // shape below.
    public List<string> PreviewUrlRequests { get; } = [];

    // REQ-722 (S-182): what UploadAsync "stored," keyed by the storage key
    // it returned — lets DownloadAsync below hand the same bytes/content
    // type back for a GET /users/me/avatar/{id}/image test, the same
    // "record what was written so a later fake call can read it back"
    // shape this fake already uses for DeletedStorageKeys, without ever
    // touching real storage.
    public Dictionary<string, (byte[] Content, string ContentType)> StoredContent { get; } = [];

    public Task<string> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        UploadedContentTypes.Add(contentType);
        var storageKey = Guid.NewGuid().ToString("N");

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        StoredContent[storageKey] = (buffer.ToArray(), contentType);

        return Task.FromResult(storageKey);
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        DeletedStorageKeys.Add(storageKey);
        StoredContent.Remove(storageKey);
        return Task.FromResult(true);
    }

    public Task<AvatarImageContent?> DownloadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(StoredContent.TryGetValue(storageKey, out var stored)
            ? new AvatarImageContent(stored.Content, stored.ContentType)
            : null);

    public Task<string> GetPreviewUrlAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        PreviewUrlRequests.Add(storageKey);
        return Task.FromResult($"https://fake-storage.test/{storageKey}");
    }
}
