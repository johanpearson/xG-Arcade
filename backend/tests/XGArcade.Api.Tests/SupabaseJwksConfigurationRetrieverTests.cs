using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols;
using XGArcade.Api.Auth;

namespace XGArcade.Api.Tests;

// ADR-0017: SupabaseJwksConfigurationRetriever is the one piece of genuinely
// new logic in the JWKS validation fix, with no other test coverage — these
// exercise it directly against a fake IDocumentRetriever (no network), per
// docs/coding-guidelines.md's "unit tests don't touch the network" rule.
public class SupabaseJwksConfigurationRetrieverTests
{
    // A real (freshly generated, not a placeholder string) RSA public key is
    // required here — JsonWebKeySet.GetSigningKeys() silently skips any key
    // whose n/e don't decode into usable RSA key material, so a hand-typed
    // fake modulus string would make this test pass for the wrong reason
    // (an empty SigningKeys collection either way).
    private static string BuildValidJwksJson(string kid)
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var n = Base64UrlEncode(parameters.Modulus!);
        var e = Base64UrlEncode(parameters.Exponent!);
        return $$"""
            {
              "keys": [
                {
                  "kty": "RSA",
                  "kid": "{{kid}}",
                  "use": "sig",
                  "alg": "RS256",
                  "n": "{{n}}",
                  "e": "{{e}}"
                }
              ]
            }
            """;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Test]
    public async Task GetConfigurationAsync_ValidJwks_ReturnsConfigurationWithSigningKeys()
    {
        var retriever = new SupabaseJwksConfigurationRetriever();
        var documentRetriever = new FakeDocumentRetriever(_ => Task.FromResult(BuildValidJwksJson("test-kid-1")));

        var configuration = await retriever.GetConfigurationAsync(
            "https://example.supabase.co/auth/v1/.well-known/jwks.json", documentRetriever, CancellationToken.None);

        Assert.That(configuration.SigningKeys, Has.Count.EqualTo(1));
        Assert.That(configuration.SigningKeys.Single().KeyId, Is.EqualTo("test-kid-1"));
    }

    [Test]
    public void GetConfigurationAsync_MalformedJson_ThrowsWithAddressAndReason()
    {
        var retriever = new SupabaseJwksConfigurationRetriever();
        var documentRetriever = new FakeDocumentRetriever(_ => Task.FromResult("<html>not json</html>"));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            retriever.GetConfigurationAsync(
                "https://example.supabase.co/auth/v1/.well-known/jwks.json", documentRetriever, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("isn't a valid JWKS"));
        Assert.That(ex.Message, Does.Contain("https://example.supabase.co/auth/v1/.well-known/jwks.json"));
    }

    [Test]
    public void GetConfigurationAsync_FetchFails_ThrowsWithAddressAndReason()
    {
        var retriever = new SupabaseJwksConfigurationRetriever();
        var documentRetriever = new FakeDocumentRetriever(_ => throw new HttpRequestException("404 Not Found"));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            retriever.GetConfigurationAsync(
                "https://example.supabase.co/auth/v1/.well-known/jwks.json", documentRetriever, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("Failed to fetch the Supabase JWKS document"));
        Assert.That(ex.Message, Does.Contain("https://example.supabase.co/auth/v1/.well-known/jwks.json"));
        Assert.That(ex.Message, Does.Contain("Auth:SupabaseJwksPath"));
    }

    private sealed class FakeDocumentRetriever(Func<string, Task<string>> getDocument) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) => getDocument(address);
    }
}
