using System.Net;
using System.Net.Http.Headers;
using System.Text;
using XGArcade.Storage.Supabase;
using XGArcade.TestSupport;

namespace XGArcade.Storage.Tests;

// REQ-722/ADR-0087 (S-180): SupabaseAvatarStorage's own request-shaping —
// same FakeHttpMessageHandler-based unit-test shape
// SupabaseAuthClientCaptchaTests (XGArcade.Core.Tests) already uses for its
// own Supabase REST client. No mocking framework
// (docs/coding-guidelines.md).
public class SupabaseAvatarStorageTests
{
    private static HttpClient BuildHttpClient(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://example.supabase.co/") };

    [Test]
    public async Task REQ722_UploadAsync_PostsToTheConfiguredBucketPath_AndReturnsAGeneratedStorageKey()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"Key":"avatars/whatever"}""");
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("fake-image-bytes"));

        var storageKey = await storage.UploadAsync(content, "image/jpeg");

        Assert.That(storageKey, Is.Not.Null.And.Not.Empty);
        Assert.That(Guid.TryParseExact(storageKey, "N", out _), Is.True, "the storage key is an opaque, generated identifier");
        Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo($"/storage/v1/object/avatars/{storageKey}"));
        Assert.That(handler.LastRequest.Content!.Headers.ContentType!.MediaType, Is.EqualTo("image/jpeg"));
    }

    [Test]
    public async Task REQ722_UploadAsync_GeneratesADifferentStorageKey_OnEveryCall()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"Key":"avatars/whatever"}""");
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var firstKey = await storage.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("a")), "image/jpeg");
        var secondKey = await storage.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("b")), "image/jpeg");

        Assert.That(firstKey, Is.Not.EqualTo(secondKey), "REQ-722: never reuse a storage key/path across uploads");
    }

    [Test]
    public void REQ722_UploadAsync_Throws_WhenSupabaseRejectsTheUpload()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.Forbidden);
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await storage.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("a")), "image/jpeg"));
    }

    [Test]
    public async Task REQ722_DeleteAsync_SendsABulkDeleteRequest_WithThePrefixesBody()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "[]");
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var result = await storage.DeleteAsync("some-key");

        Assert.That(result, Is.True);
        Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Delete));
        Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/storage/v1/object/avatars"));
        Assert.That(handler.LastRequestBody, Does.Contain("some-key"));
    }

    // Same "already gone counts as success" contract as
    // SupabaseAuthClient.DeleteUserAsync (REQ-710/ADR-0026).
    [Test]
    public async Task REQ722_DeleteAsync_ReturnsTrue_WhenTheKeyIsAlreadyGone()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.NotFound);
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var result = await storage.DeleteAsync("already-gone-key");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task REQ722_DeleteAsync_ReturnsFalse_OnAGenuineFailure()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.InternalServerError);
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var result = await storage.DeleteAsync("some-key");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task REQ722_DownloadAsync_ReturnsTheBytesAndContentType_OnASuccessfulResponse()
    {
        var imageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(imageBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
        }));
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var result = await storage.DownloadAsync("some-key");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Content, Is.EqualTo(imageBytes));
        Assert.That(result.ContentType, Is.EqualTo("image/png"));
    }

    [Test]
    public async Task REQ722_DownloadAsync_ReturnsNull_OnA404_RatherThanThrowing()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.NotFound);
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var result = await storage.DownloadAsync("unknown-key");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task REQ722_DownloadAsync_RequestsTheExpectedPath_BucketPlusStorageKey()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-image-bytes"))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
            },
        }));
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        await storage.DownloadAsync("some-key");

        Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/storage/v1/object/avatars/some-key"));
    }

    // ---- REQ-517: GetPreviewUrlAsync ----------------------------------------

    [Test]
    public async Task REQ517_GetPreviewUrlAsync_PostsToTheSignEndpoint_WithAnExpiresInBody()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            HttpStatusCode.OK, """{"signedURL":"/object/sign/avatars/some-key?token=abc123"}""");
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        var url = await storage.GetPreviewUrlAsync("some-key");

        Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/storage/v1/object/sign/avatars/some-key"));
        Assert.That(handler.LastRequestBody, Does.Contain("expiresIn"));
        Assert.That(url, Is.EqualTo("https://example.supabase.co/storage/v1/object/sign/avatars/some-key?token=abc123"),
            "REQ-517: the relative signedURL Supabase returns must be resolved to an absolute URL an admin's browser can load directly");
    }

    [Test]
    public void REQ517_GetPreviewUrlAsync_Throws_WhenSupabaseRejectsTheSignRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.Forbidden);
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        Assert.ThrowsAsync<HttpRequestException>(async () => await storage.GetPreviewUrlAsync("some-key"));
    }

    [Test]
    public void REQ517_GetPreviewUrlAsync_Throws_WhenTheResponseHasNoSignedUrl()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{}""");
        var storage = new SupabaseAvatarStorage(BuildHttpClient(handler), new SupabaseAvatarBucketOptions("avatars"));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await storage.GetPreviewUrlAsync("some-key"));
    }
}
