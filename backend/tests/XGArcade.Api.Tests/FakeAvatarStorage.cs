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

    public Task<string> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        UploadedContentTypes.Add(contentType);
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        DeletedStorageKeys.Add(storageKey);
        return Task.FromResult(true);
    }

    public Task<string> GetPreviewUrlAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        PreviewUrlRequests.Add(storageKey);
        return Task.FromResult($"https://fake-storage.test/{storageKey}");
    }
}
