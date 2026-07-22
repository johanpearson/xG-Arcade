using System.Net;
using System.Text.Json;
using XGArcade.Core.Auth;
using XGArcade.TestSupport;

namespace XGArcade.Core.Tests.Auth;

// REQ-717's 2026-07-21 "Bot-check (captcha) for guest creation" addition /
// ADR-0037: SupabaseAuthClient's own unit coverage for
// ReadFailureResultAsync's IsCaptchaRejection detection -- the piece
// AuthEndpointTests.cs's REQ717_Guest_Post_ReturnsDistinctCaptchaRejection_*
// tests don't exercise at all (those stub ISupabaseAuthClient wholesale via
// FakeSupabaseAuthClient, setting IsCaptchaRejection directly, never routing
// through SupabaseAuthClient's own error-body parsing). No mocking
// framework (docs/coding-guidelines.md) -- a fake HttpMessageHandler stands
// in for Supabase Auth's REST API, same pattern
// XGArcade.DataSync.Tests/Wikidata/WikidataClientTests.cs already uses for
// WikidataClient.
public class SupabaseAuthClientCaptchaTests
{
    private static HttpClient BuildHttpClient(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://example.supabase.co/") };

    // Gap: Supabase Auth (GoTrue)'s documented machine-readable error code
    // ("captcha_failed" in an "error_code" field) is the primary,
    // more-reliable detection signal -- checked first per
    // ReadFailureResultAsync's own comment. Deliberately uses a message that
    // does NOT contain the word "captcha" at all, so this test can only pass
    // if the error_code path itself sets IsCaptchaRejection, not the
    // substring fallback (which would also happen to fire on a
    // "captcha"-containing message, conflating the two paths).
    [Test]
    public async Task REQ717_SignInAnonymouslyAsync_DetectsCaptchaRejection_ByErrorCode_WhenMessageHasNoCaptchaWording()
    {
        const string json = """{ "msg": "Request disallowed", "error_code": "captcha_failed" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.SignInAnonymouslyAsync("an-invalid-token");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.True);
    }

    // Gap: the case-insensitive substring fallback on the human-readable
    // message, used when Supabase's response has no "error_code" field at
    // all (whichever Supabase Auth version is actually deployed) -- its own
    // distinct case, not just incidentally covered by the error_code test
    // above.
    [Test]
    public async Task REQ717_SignInAnonymouslyAsync_DetectsCaptchaRejection_ByMessageSubstring_WhenErrorCodeIsAbsent()
    {
        const string json = """{ "msg": "captcha verification process failed" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.SignInAnonymouslyAsync("an-invalid-token");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.True);
    }

    // The important negative case: a failure with neither an error_code nor
    // any "captcha" wording at all is never misclassified as a captcha
    // rejection.
    [Test]
    public async Task REQ717_SignInAnonymouslyAsync_DoesNotSetIsCaptchaRejection_ForAnUnrelatedFailure()
    {
        const string json = """{ "msg": "Anonymous sign-ins are disabled." }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.SignInAnonymouslyAsync("some-token");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.False);
    }

    // REQ-717/ADR-0037's request-shape contract: the token is forwarded
    // exactly as gotrue_meta_security.captcha_token, never renamed/
    // restructured, and no other field this backend independently verifies
    // is added.
    [Test]
    public async Task REQ717_SignInAnonymouslyAsync_SendsCaptchaTokenAsGotrueMetaSecurityCaptchaTokenField()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{ "access_token": "at", "refresh_token": "rt", "user": { "id": "11111111-1111-1111-1111-111111111111" } }""");
        var client = new SupabaseAuthClient(BuildHttpClient(handler), new SupabaseServiceRoleKey("unused"));

        await client.SignInAnonymouslyAsync("the-turnstile-token");

        Assert.That(handler.LastRequestBody, Is.Not.Null);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var captchaToken = document.RootElement
            .GetProperty("gotrue_meta_security")
            .GetProperty("captcha_token")
            .GetString();
        Assert.That(captchaToken, Is.EqualTo("the-turnstile-token"));
    }

    // ---- REQ-717/ADR-0037 scope regression: IsCaptchaRejection must never
    // fire for SignUp/SignInWithPassword/RefreshToken/LinkEmailPassword's
    // ordinary, realistic failure messages -- this captcha check is scoped
    // to POST /auth/guest only (REQ-717/ADR-0037's explicit scope), and
    // ReadFailureResultAsync/IsCaptchaRejection is shared plumbing computed
    // for every call on this client, not just SignInAnonymouslyAsync. A
    // false positive here would silently leak a captcha-shaped response out
    // of an endpoint that was never supposed to have one. ----

    [Test]
    public async Task REQ717_SignUpAsync_NeverSetsIsCaptchaRejection_ForItsOwnOrdinaryFailure()
    {
        const string json = """{ "msg": "User already registered" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.SignUpAsync("player@example.com", "a-reasonable-password");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.False);
    }

    [Test]
    public async Task REQ717_SignInWithPasswordAsync_NeverSetsIsCaptchaRejection_ForItsOwnOrdinaryFailure()
    {
        const string json = """{ "msg": "Invalid login credentials" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.SignInWithPasswordAsync("player@example.com", "the-wrong-password");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.False);
    }

    [Test]
    public async Task REQ717_RefreshTokenAsync_NeverSetsIsCaptchaRejection_ForItsOwnOrdinaryFailure()
    {
        const string json = """{ "msg": "Invalid Refresh Token: Already Used" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.RefreshTokenAsync("an-already-used-refresh-token");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.False);
    }

    [Test]
    public async Task REQ717_LinkEmailPasswordAsync_NeverSetsIsCaptchaRejection_ForItsOwnOrdinaryFailure()
    {
        const string json = """{ "msg": "Email address already in use" }""";
        var client = new SupabaseAuthClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.BadRequest, json)), new SupabaseServiceRoleKey("unused"));

        var result = await client.LinkEmailPasswordAsync("a-guests-access-token", "already-used@example.com", "a-reasonable-password");

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsCaptchaRejection, Is.False);
    }
}
